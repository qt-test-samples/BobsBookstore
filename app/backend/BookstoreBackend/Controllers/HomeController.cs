﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Amazon.Extensions.CognitoAuthentication;
using BobsBookstore.DataAccess.Repository.Interface.WelcomePageInterface;
using BookstoreBackend.ViewModel.UpdateBooks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace BookstoreBackend.Controllers
{
    public class HomeController : Controller
    {
        private readonly ICustomAdminPage _customAdmin;
        private readonly SignInManager<CognitoUser> _signInManager;
        private readonly UserManager<CognitoUser> _userManager;

        public HomeController(ICustomAdminPage customAdmin, 
                              SignInManager<CognitoUser> signInManager,
                              UserManager<CognitoUser> userManager)
        {
            _customAdmin = customAdmin;
            _signInManager = signInManager;
            _userManager = userManager;
        }

        public IActionResult Index()
        {
            return View();
        }

        public async Task<IActionResult> WelcomePageAsync(string sortByValue)
        {
            var user = await _userManager.GetUserAsync(User);
            var adminUsername = user.Username;

            var bookUpdates = new LatestUpdates();

            // the sortByValue input for different sorting parameters. 
            var orderByMethods = new List<string> { "OrderDetailPrice", "price_desc", "date", "date_desc" };
            // assigns ViewBag a default value just to initialize it 
            ViewBag.SortPrice = "OrderDetailPrice";
            ViewBag.SortDate = "date";
            ViewBag.SortStatus = "status";
            ViewBag.PriceArrow = "▲";
            ViewBag.DateArrow = "▲";
            ViewBag.StatusArrow = "▲";
            // get the date range from the user
            var dateMinRange = 0;
            var dateMaxRange = 5;

            //Get books updated by current user
            var userBooks = _customAdmin.GetUserUpdatedBooks(adminUsername);
            bookUpdates.UserBooks = userBooks.Result;
            // get recent books updated globally
            bookUpdates.NotUserBooks = _customAdmin.OtherUpdatedBooks(adminUsername).Result;
            // get important orders
            bookUpdates.ImpOrders = _customAdmin.GetImportantOrders(dateMaxRange, dateMinRange).Result;
            if (bookUpdates.UserBooks == null || bookUpdates.NotUserBooks == null || bookUpdates.ImpOrders == null)
                throw new ArgumentNullException("The LatestUpdates View model cannot have a null value", "bookupdates");

            if (orderByMethods.Contains(sortByValue))
            {
                // assigns ViewBag.Sort with the opposite value of sortByValue
                if (sortByValue == "OrderDetailPrice" || sortByValue == "price_desc")
                    ViewBag.SortPrice = sortByValue == "OrderDetailPrice" ? "price_desc" : "OrderDetailPrice";
                else if (sortByValue == "date" || sortByValue == "date_desc")
                    ViewBag.SortDate = sortByValue == "date" ? "date_desc" : "date";
                else if (sortByValue == "status" || sortByValue == "status_desc")
                    ViewBag.SortDate = sortByValue == "status" ? "status_desc" : "status";
                //to change the arrow on the html anchors based on asc or desc
                if (ViewBag.SortPrice == "OrderDetailPrice")
                    ViewBag.PriceArrow = "▲";
                else if (ViewBag.SortPrice == "price_desc")
                    ViewBag.PriceArrow = "▼";
                if (ViewBag.SortDate == "date")
                    ViewBag.DateArrow = "▲";
                else if (ViewBag.SortDate == "date_desc")
                    ViewBag.DateArrow = "▼";
                if (ViewBag.SortStatus == "status")
                    ViewBag.DateArrow = "▲";
                else if (ViewBag.SortStatus == "status_desc")
                    ViewBag.DateArrow = "▼";

                bookUpdates.ImpOrders = _customAdmin.SortTable(bookUpdates.ImpOrders, sortByValue);
                return View(bookUpdates);
            }

            return View(bookUpdates);
        }

        public IActionResult Logout()
        {
            return View();
        }

        public IActionResult Privacy()
        {
            return View();
        }

        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
        {
            return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
        }
    }
}
