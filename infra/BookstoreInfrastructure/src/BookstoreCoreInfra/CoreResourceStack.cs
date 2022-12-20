using System.Collections.Generic;
using Amazon.CDK;
using Amazon.CDK.AWS.CloudFront;
using Amazon.CDK.AWS.Cognito;
using Amazon.CDK.AWS.EC2;
using Amazon.CDK.AWS.IAM;
using Amazon.CDK.AWS.RDS;
using Amazon.CDK.AWS.S3;
using Amazon.CDK.AWS.SSM;
using Amazon.CDK.AWS.Logs;
using Constructs;
using Amazon.CDK.AWS.EKS;
using System.Xml;

namespace BookstoreCoreInfra;

// Defines the core resources that make up the Bob's Used Books application when deployed to the
// AWS Cloud.
public class CoreResourceStack : Stack
{
    // Settings such as the CloudFront distribution domain, S3 bucket name, and user pools
    // for the web apps are placed into Systems Manager Parameter Store.
    // This will allow us to read all the parameters, in one pass, in the customer-facing and
    // admin apps using the AWS-DotNet-Extensions-Configuration package
    // (https://github.com/aws/aws-dotnet-extensions-configuration). This package reads all
    // parameters beneath a parameter key root and injects them into the app's configuration at
    // runtime, just as if they were statically held in appsettings.json. This is why the
    // application roles defined below need permissions to call the ssm:GetParametersByPath
    // action.
    private const string customerSiteSettingsRoot = "BobsUsedBookCustomerStore";
    private const string adminSiteSettingsRoot = "BobsUsedBookAdminStore";
    private const string bookstoreDbCredentialsParameter = "bookstoredb";
    private const string customerSiteUserPoolName = "BobsBookstoreCustomerUsers";
    private const string adminSiteUserPoolName = "BobsBookstoreAdminUsers";
    private const string adminSiteLogGroupName = "BobsBookstoreAdminLogs";
    private const string customerSiteLogGroupName = "BobsBookstoreCustomerLogs";

    private const string customerSiteUserPoolCallbackUrlRoot = "https://localhost:4001";
    private const string adminSiteUserPoolCallbackUrlRoot = "https://localhost:5001";

    private const double DatabasePort = 1433;

    internal CoreResourceStack(Construct scope, string id, IStackProps props = null) : base(scope, id, props)
    {
        // Create a new vpc spanning two AZs and with public and private subnets
        // to host the application resources
        var vpc = new Vpc(this, "BobsBookstoreVpc", new VpcProps
        {
            IpAddresses = IpAddresses.Cidr("10.0.0.0/16"),
            // Cap at 2 AZs in case we are deployed to a region with only 2
            MaxAzs = 2,
            // We do not need a NAT gateway for this sample application, so remove to reduce cost
            NatGateways = 0,
            SubnetConfiguration = new[]
            {
                new SubnetConfiguration
                {
                    CidrMask = 24,
                    SubnetType = SubnetType.PUBLIC,
                    Name = "BookstorePublicSubnet"
                },
                new SubnetConfiguration
                {
                    CidrMask = 24,
                    SubnetType = SubnetType.PRIVATE_ISOLATED,
                    Name = "BookstorePrivateSubnet"
                }
            }
        });

        //=========================================================================================
        // Create security groups in the VPC for the admin and customer websites that permit access
        // on port 80

        var customerSiteSG = new SecurityGroup(this, "CustomerSiteSecurityGroup", new SecurityGroupProps
        {
            Vpc = vpc,
            Description = "Allow HTTP access to sample app customer website",
            AllowAllOutbound = true
        });
        customerSiteSG.AddIngressRule(Peer.AnyIpv4(), Port.Tcp(80), "HTTP access");

        var adminSiteSG = new SecurityGroup(this, "AdminSiteSecurityGroup", new SecurityGroupProps
        {
            Vpc = vpc,
            Description = "Allow HTTP access to sample app admin website",
            AllowAllOutbound = true
        });
        adminSiteSG.AddIngressRule(Peer.AnyIpv4(), Port.Tcp(80), "HTTP access");

        //=========================================================================================
        // Create a Microsoft SQL Server database instance, on default port, and placed
        // into a private subnet so that it cannot be accessed from the public internet.

        // First, a security group that will permit access from the customer and admin apps, using
        // their security groups.
        var dbSG = new SecurityGroup(this, "DatabaseSecurityGroup", new SecurityGroupProps
        {
            Vpc = vpc,
            Description = "Allow access to the SQL Server instance from the admin and customer website instances",
        });
        dbSG.AddIngressRule(adminSiteSG, Port.Tcp(DatabasePort), "Admin app to SQL Server");
        dbSG.AddIngressRule(customerSiteSG, Port.Tcp(DatabasePort), "Customer app to SQL Server");

        var db = new DatabaseInstance(this, "BookstoreSqlDb", new DatabaseInstanceProps
        {
            Vpc = vpc,
            VpcSubnets = new SubnetSelection
            {
                // We do not need egress connectivity to the internet for this sample. This
                // eliminates the need for a NAT gateway.
                SubnetType = SubnetType.PRIVATE_ISOLATED
            },
            // SQL Server 2017 Express Edition, in conjunction with a db.t2.micro instance type,
            // fits inside the free tier for new accounts
            Engine = DatabaseInstanceEngine.SqlServerEx(new SqlServerExInstanceEngineProps
            {
                Version = SqlServerEngineVersion.VER_14
            }),
            Port = DatabasePort,
            SecurityGroups = new[]
            {
                dbSG
            },
            InstanceType = InstanceType.Of(InstanceClass.BURSTABLE2, InstanceSize.MICRO),

            InstanceIdentifier = bookstoreDbCredentialsParameter,

            // As this is a sample app, turn off automated backups to avoid any storage costs
            // of automated backup snapshots. It also helps the stack launch a little faster by
            // avoiding an initial backup.
            BackupRetention = Duration.Seconds(0)
        });

        // The secret, in Secrets Manager, holds the auto-generated database credentials. Because
        // the secret name will have a random string suffix, we add a deterministic parameter in
        // Systems Manager to contain the actual secret name.
        _ = new StringParameter(this, "BobsBookstoreDbSecret", new StringParameterProps
        {
            ParameterName = $"/{bookstoreDbCredentialsParameter}/dbsecretsname",
            StringValue = db.Secret.SecretName
        });

        //=========================================================================================
        // A non-publicly accessible Amazon S3 bucket is used to store the cover
        // images for books.
        // As this is a sample application, we are configuring the bucket
        // to be deleted when the stack is deleted
        // to avoid storage charges - EVEN IF IT CONTAINS DATA - BEWARE! 
        var bookstoreBucket = new Bucket(this, "BobsBookstoreBucket", new BucketProps
        {
            // !DO NOT USE THESE TWO SETTINGS FOR PRODUCTION DEPLOYMENTS - YOU WILL LOSE DATA
            // WHEN THE STACK IS DELETED!
            AutoDeleteObjects = true,
            RemovalPolicy = RemovalPolicy.DESTROY
        });

        _ = new StringParameter(this, "S3BucketName", new StringParameterProps
        {
            ParameterName = $"/{adminSiteSettingsRoot}/AWS/BucketName",
            StringValue = bookstoreBucket.BucketName
        });

        // Access to the bucket is only granted to traffic coming a CloudFront distribution
        var cloudfrontOAI = new OriginAccessIdentity(this, "cloudfront-OAI");

        var policyProps = new PolicyStatementProps
        {
            Actions = new [] { "s3:GetObject" },
            Resources = new [] { bookstoreBucket.ArnForObjects("*") },
            Principals = new []
            {
                new CanonicalUserPrincipal
                (
                    cloudfrontOAI.CloudFrontOriginAccessIdentityS3CanonicalUserId
                )
            }
        };

        bookstoreBucket.AddToResourcePolicy(new PolicyStatement(policyProps));

        // Place a CloudFront distribution in front of the storage bucket. S3 will only respond to
        // requests for objects if that request came from the CloudFront distribution.
        var distProps = new CloudFrontWebDistributionProps
        {
            OriginConfigs = new []
            {
                new SourceConfiguration
                {
                    S3OriginSource = new S3OriginConfig
                    {
                        S3BucketSource = bookstoreBucket,
                        OriginAccessIdentity = cloudfrontOAI
                    },
                    Behaviors = new []
                    {
                        new Behavior
                        {
                            IsDefaultBehavior = true,
                            Compress = true,
                            AllowedMethods = CloudFrontAllowedMethods.GET_HEAD_OPTIONS
                        }
                    }
                }
            },
            // Require HTTPS between viewer and CloudFront; CloudFront to
            // origin (the bucket) will use HTTP but could also be set to require HTTPS
            ViewerProtocolPolicy = ViewerProtocolPolicy.REDIRECT_TO_HTTPS
        };

        var distribution = new CloudFrontWebDistribution(this, "SiteDistribution", distProps);

        _ = new StringParameter(this, "CDN", new StringParameterProps
        {
            ParameterName = $"/{adminSiteSettingsRoot}/AWS/CloudFrontDomain",
            StringValue = $"https://{distribution.DistributionDomainName}"
        });

        //=========================================================================================
        // Configure a Cognito user pool for the customer web site, to hold customer registrations.
        var customerUserPool = new UserPool(this, "BobsBookstoreCustomerUserPool", new UserPoolProps
        {
            UserPoolName = customerSiteUserPoolName,
            SelfSignUpEnabled = true,
            StandardAttributes = new StandardAttributes
            {
                Email = new StandardAttribute      { Required = true },
                FamilyName = new StandardAttribute { Required = true },
                GivenName = new StandardAttribute  { Required = true }
            },
            AutoVerify = new AutoVerifiedAttrs { Email = true },
            CustomAttributes = new Dictionary<string, ICustomAttribute>
            {
                { "AddressLine1", new StringAttribute() },
                { "AddressLine2", new StringAttribute() },
                { "City", new StringAttribute() },
                { "State", new StringAttribute() },
                { "Country", new StringAttribute() },
                { "ZipCode", new StringAttribute() }
            }
        });

        // The pool client controls user registration and sign-in from the customer-facing website.
        var customerUserPoolClient
            = customerUserPool.AddClient("BobsBookstorePoolCustomerClient", new UserPoolClientProps
            {
                UserPool = customerUserPool,
                GenerateSecret = false,
                ReadAttributes = new ClientAttributes()
                    .WithCustomAttributes("AddressLine1", "AddressLine2", "City", "State", "Country", "ZipCode")
                    .WithStandardAttributes(new StandardAttributesMask
                    {
                        GivenName = true,
                        FamilyName = true,
                        Address = true,
                        PhoneNumber = true,
                        Email = true
                    }),
                SupportedIdentityProviders = new []
                {
                    UserPoolClientIdentityProvider.COGNITO
                },
                OAuth = new OAuthSettings
                {
                    Flows = new OAuthFlows
                    {
                        AuthorizationCodeGrant = true,
                        ImplicitCodeGrant = true
                    },
                    Scopes = new []
                    {
                        OAuthScope.OPENID,
                        OAuthScope.EMAIL,
                        OAuthScope.PHONE,
                        OAuthScope.PROFILE,
                        OAuthScope.COGNITO_ADMIN
                    },
                    CallbackUrls = new []
                    {
                        $"{customerSiteUserPoolCallbackUrlRoot}/signin-oidc"
                    },
                    LogoutUrls = new []
                    {
                        $"{customerSiteUserPoolCallbackUrlRoot}/"
                    }
                }
            });

        var customerUserPoolDomain = customerUserPool.AddDomain("CustomerUserPoolDomain", new UserPoolDomainOptions
        {
            CognitoDomain = new CognitoDomainOptions
            {
                // The prefix must be unique across the AWS Region in which the pool is created
                DomainPrefix = $"bobs-bookstore-{Account}"
            }
        });

        customerUserPoolDomain.SignInUrl(customerUserPoolClient, new SignInUrlOptions
        {
            RedirectUri = $"{customerSiteUserPoolCallbackUrlRoot}/signin-oidc"
        });

        _ = new []
        {
            new StringParameter(this, "BookstoreCustomerUserPoolClientId", new StringParameterProps
            {
                ParameterName = $"/{customerSiteSettingsRoot}/Authentication/Cognito/ClientId",
                StringValue = customerUserPoolClient.UserPoolClientId
            }),

            new StringParameter(this, "BookstoreCustomerUserPoolMetadataAddress", new StringParameterProps
            {
                ParameterName = $"/{customerSiteSettingsRoot}/Authentication/Cognito/MetadataAddress",
                StringValue = $"https://cognito-idp.{Region}.amazonaws.com/{customerUserPool.UserPoolId}/.well-known/openid-configuration"
            }),

            new StringParameter(this, "BookstoreCustomerUserPoolCognitoDomain", new StringParameterProps
            {
                ParameterName = $"/{customerSiteSettingsRoot}/Authentication/Cognito/CognitoDomain",
                StringValue = customerUserPoolDomain.BaseUrl()
            })
        };

        //=========================================================================================
        // The admin site, used by bookstore staff, has its own user pool
        var adminSiteUserPool = new UserPool(this, "BobsBookstoreAdminUserPool", new UserPoolProps
        {
            UserPoolName = adminSiteUserPoolName,
            SelfSignUpEnabled = false,
            StandardAttributes = new StandardAttributes
            {
                Email = new StandardAttribute
                {
                    Required = true
                }
            },
            AutoVerify = new AutoVerifiedAttrs { Email = true }
        });

        var adminSiteUserPoolClient = adminSiteUserPool.AddClient("BobsBookstorePoolAdminClient", new UserPoolClientProps
        {
            UserPool = adminSiteUserPool,
            GenerateSecret = false,
            SupportedIdentityProviders = new []
            {
                UserPoolClientIdentityProvider.COGNITO
            },
            OAuth = new OAuthSettings
            {
                Flows = new OAuthFlows
                {
                    AuthorizationCodeGrant = true,
                    ImplicitCodeGrant = true
                },
                Scopes = new []
                {
                    OAuthScope.OPENID,
                    OAuthScope.EMAIL,
                    OAuthScope.COGNITO_ADMIN
                },
                CallbackUrls = new []
                {
                    $"{adminSiteUserPoolCallbackUrlRoot}/signin-oidc"
                },
                LogoutUrls = new []
                {
                    $"{adminSiteUserPoolCallbackUrlRoot}/"
                }
            }
        });

        var adminSiteUserPoolDomain = adminSiteUserPool.AddDomain("AdminUserPoolDomain", new UserPoolDomainOptions
        {
            CognitoDomain = new CognitoDomainOptions
            {
                // The prefix must be unique across the AWS Region in which the pool is created
                DomainPrefix = $"bobs-bookstore-admin-{Account}"
            }
        });

        adminSiteUserPoolDomain.SignInUrl(adminSiteUserPoolClient, new SignInUrlOptions
        {
            RedirectUri = $"{adminSiteUserPoolCallbackUrlRoot}/signin-oidc"
        });

        _ = new []
        {
            new StringParameter(this, "BookstoreAdminUserPoolClientId", new StringParameterProps
            {
                ParameterName = $"/{adminSiteSettingsRoot}/Authentication/Cognito/ClientId",
                StringValue = adminSiteUserPoolClient.UserPoolClientId
            }),

            new StringParameter(this, "BookstoreAdminUserPoolMetadataAddress", new StringParameterProps
            {
                ParameterName = $"/{adminSiteSettingsRoot}/Authentication/Cognito/MetadataAddress",
                StringValue = $"https://cognito-idp.{Region}.amazonaws.com/{adminSiteUserPool.UserPoolId}/.well-known/openid-configuration"
            }),

            new StringParameter(this, "BookstoreAdminUserPoolCognitoDomain", new StringParameterProps
            {
                ParameterName = $"/{adminSiteSettingsRoot}/Authentication/Cognito/CognitoDomain",
                StringValue = adminSiteUserPoolDomain.BaseUrl()
            })
        };

        //=========================================================================================
        // Create an application role for the admin website, seeded with the Systems Manager
        // permissions allowing future management from Systems Manager and remote access
        // from the console. Also add the CodeDeploy service role allowing deployments through
        // CodeDeploy if we wish. The trust relationship to EC2 enables the running application
        // to obtain temporary, auto-rotating credentials for calls to service APIs made by the
        // AWS SDK for .NET, without needing to place credentials onto the compute host.
        var adminAppRole = new Role(this, "AdminBookstoreApplicationRole", new RoleProps
        {
            AssumedBy = new ServicePrincipal("ec2.amazonaws.com"),
            ManagedPolicies = new []
            {
                ManagedPolicy.FromAwsManagedPolicyName("AmazonSSMManagedInstanceCore"),
                ManagedPolicy.FromAwsManagedPolicyName("service-role/AWSCodeDeployRole")
            }
        });

        // Access to read parameters by path is not in the AmazonSSMManagedInstanceCore
        // managed policy
        adminAppRole.AddToPolicy(new PolicyStatement(new PolicyStatementProps
        {
            Effect = Effect.ALLOW,
            Actions = new [] { "ssm:GetParametersByPath" },
            Resources = new []
            {
                Arn.Format(new ArnComponents
                {
                    Service = "ssm",
                    Resource = "parameter",
                    ResourceName = $"{bookstoreDbCredentialsParameter}/*"
                }, this),

                Arn.Format(new ArnComponents
                {
                    Service = "ssm",
                    Resource = "parameter",
                    ResourceName = $"{adminSiteSettingsRoot}/*"
                }, this)
            }
        }));

        // Provide permission to the app to allow access to Cognito user pools
        adminAppRole.AddToPolicy(new PolicyStatement(new PolicyStatementProps
        {
            Effect = Effect.ALLOW,
            Actions = new []
            {
                "cognito-idp:AdminListGroupsForUser",
                "cognito-idp:AdminGetUser",
                "cognito-idp:DescribeUserPool",
                "cognito-idp:DescribeUserPoolClient",
                "cognito-idp:ListUsers"
            },

            Resources = new []
            {
                Arn.Format(new ArnComponents
                {
                    Service = "cognito-idp",
                    Resource = "userpool",
                    ResourceName = adminSiteUserPool.UserPoolId
                }, this)
            }
        }));

        // Provide permission to allow access to Amazon Rekognition for processing uploaded
        // book images. Credentials for the calls will be provided courtesy of the application
        // role defined above, and the trust relationship with EC2.
        adminAppRole.AddToPolicy(new PolicyStatement(new PolicyStatementProps
        {
            Effect = Effect.ALLOW,
            Actions = new [] { "rekognition:DetectModerationLabels" },
            Resources = new [] { "*" }
        }));

        // Add permissions for the app to retrieve the database password in Secrets Manager
        db.Secret.GrantRead(adminAppRole);

        // Add permissions to the app to access the S3 bucket
        bookstoreBucket.GrantReadWrite(adminAppRole);

        // Create an Amazon CloudWatch log group for the admin website
        _ = new CfnLogGroup(this, "BobsBookstoreAdminLogGroup", new CfnLogGroupProps
        {
            LogGroupName = adminSiteLogGroupName
        });

        // Add permissions to write logs
        adminAppRole.AddToPolicy(new PolicyStatement(new PolicyStatementProps
        {
            Effect = Effect.ALLOW,
            Actions = new [] 
            {
                "logs:DescribeLogGroups",
                "logs:CreateLogGroup",
                "logs:CreateLogStream"
            },
            Resources = new [] 
            {
                "arn:aws:logs:*:*:log-group:*"
            }
        }));

        adminAppRole.AddToPolicy(new PolicyStatement(new PolicyStatementProps
        {
            Effect = Effect.ALLOW,
            Actions = new [] 
            {
                "logs:PutLogEvents",
            },
            Resources = new [] 
            {
                "arn:aws:logs:*:*:log-group:*:log-stream:*",
            }
        }));

        // Add permissions to send email via Amazon SES's API
        adminAppRole.AddToPolicy(new PolicyStatement(new PolicyStatementProps
        {
            Effect = Effect.ALLOW,
            Actions = new [] 
            {
                "ses:SendEmail"
            },
            Resources = new []
            {
                "*"
            },
            Conditions = new Dictionary<string, object>
            {
                { "StringEquals", 
                    new Dictionary<string, object> 
                    {
                        { "ses:ApiVersion", "2" } 
                    }
                }
            }
        }));

        //=========================================================================================
        // As with the admin website, create an application role scoping permissions for service
        // API calls and resources needed by the customer-facing website, and providing temporary
        // credentials via a trust relationship
        var customerAppRole = new Role(this, "CustomerBookstoreApplicationRole", new RoleProps
        {
            AssumedBy = new ServicePrincipal("ec2.amazonaws.com"),
            ManagedPolicies = new []
            {
                ManagedPolicy.FromAwsManagedPolicyName("AmazonSSMManagedInstanceCore"),
                ManagedPolicy.FromAwsManagedPolicyName("service-role/AWSCodeDeployRole")
            }
        });

        // Access to read parameters by path is not in the AmazonSSMManagedInstanceCore
        // managed policy
        customerAppRole.AddToPolicy(new PolicyStatement(new PolicyStatementProps
        {
            Effect = Effect.ALLOW,
            Actions = new [] { "ssm:GetParametersByPath" },
            Resources = new []
            {
                Arn.Format(new ArnComponents
                {
                    Service = "ssm",
                    Resource = "parameter",
                    ResourceName = $"{bookstoreDbCredentialsParameter}/*"
                }, this),

                Arn.Format(new ArnComponents
                {
                    Service = "ssm",
                    Resource = "parameter",
                    ResourceName = $"{customerSiteSettingsRoot}/*"
                }, this)
            }
        }));

        // Provide permission to the app to allow access to Cognito user pools
        customerAppRole.AddToPolicy(new PolicyStatement(new PolicyStatementProps
        {
            Effect = Effect.ALLOW,
            Actions = new []
            {
                "cognito-idp:AdminListGroupsForUser",
                "cognito-idp:AdminGetUser",
                "cognito-idp:DescribeUserPool",
                "cognito-idp:DescribeUserPoolClient",
                "cognito-idp:ListUsers"
            },

            Resources = new []
            {
                Arn.Format(new ArnComponents
                {
                    Service = "cognito-idp",
                    Resource = "userpool",
                    ResourceName = customerUserPool.UserPoolId
                }, this)
            }
        }));

        // Add permissions for the app to retrieve the database password in Secrets Manager
        db.Secret.GrantRead(customerAppRole);

        // Add permissions to the app to access the S3 bucket
        bookstoreBucket.GrantReadWrite(customerAppRole);

        // Create a separate log group for the customer site
        _ = new CfnLogGroup(this, "BobsBookstoreCustomerLogGroup", new CfnLogGroupProps
        {
            LogGroupName = customerSiteLogGroupName
        });

        // Add permissions to write logs
        customerAppRole.AddToPolicy(new PolicyStatement(new PolicyStatementProps
        {
            Effect = Effect.ALLOW,
            Actions = new [] 
            {
                "logs:DescribeLogGroups",
                "logs:CreateLogGroup",
                "logs:PutLogEvents",
                "logs:CreateLogStream"
            },
            Resources = new [] 
            {
                $"arn:aws:logs:*:*:log-group:*",
            }
        }));

        customerAppRole.AddToPolicy(new PolicyStatement(new PolicyStatementProps
        {
            Effect = Effect.ALLOW,
            Actions = new [] 
            {
                "logs:PutLogEvents",
            },
            Resources = new [] 
            {
                $"arn:aws:logs:*:*:log-group:*:log-stream:*",
            }
        }));

        // Create instance profiles wrapping the roles, which can be used later when the app
        // is deployed to Elastic Beanstalk or EC2 compute hosts
        _ = new CfnInstanceProfile(this, "AdminBobsBookstoreInstanceProfile", new CfnInstanceProfileProps
        {
            Roles = new [] { adminAppRole.RoleName}
        });
        _ = new CfnInstanceProfile(this, "CustomerBobsBookstoreInstanceProfile", new CfnInstanceProfileProps
        {
            Roles = new [] { customerAppRole.RoleName}
        });
    }
}