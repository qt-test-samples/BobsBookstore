﻿using BOBS_Backend.Models.Book;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;

namespace BOBS_Backend.Models.Order
{
    public class OrderDetail
    {
        /*
         * OrderDetail Model
         * Object reference indicates relationship to other Tables
         */
        [Key]
        public long OrderDetail_Id { get; set; }

        // Many to One Relationship
        public Order Order { get; set; }

        // Many To One Relationship
        public Book.Book Book { get; set; }

        // Many to One Relationship
        public Price Price { get; set; }

        public double price { get; set; }

        public int quantity { get; set; }
    }
}
