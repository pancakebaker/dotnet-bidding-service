// <copyright file="SaleMode.cs" company="Distributed Bidding Auction Platform">
// Copyright (c) Distributed Bidding Auction Platform. Licensed under the MIT license.
// </copyright>
namespace bidding_service.Domain;

/// <summary>
/// Defines how an auction may be completed.
/// </summary>
public enum SaleMode
{
    /// <summary>
    /// The auction accepts ordinary bids only.
    /// </summary>
    AuctionOnly = 0,

    /// <summary>
    /// The auction is completed through an explicit Buy Now operation only.
    /// </summary>
    BuyNowOnly = 1,

    /// <summary>
    /// The auction accepts ordinary bids and an explicit Buy Now operation.
    /// </summary>
    AuctionAndBuyNow = 2
}
