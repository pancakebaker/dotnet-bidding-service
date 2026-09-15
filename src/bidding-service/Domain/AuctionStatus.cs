// <copyright file="AuctionStatus.cs" company="Distributed Bidding Auction Platform">
// Copyright (c) Distributed Bidding Auction Platform. Licensed under the MIT license.
// </copyright>
namespace bidding_service.Domain;

/// <summary>
/// Defines the lifecycle states an auction can occupy.
/// </summary>
public enum AuctionStatus
{
    /// <summary>
    /// The auction is configured but has not opened for bidding.
    /// </summary>
    Scheduled = 0,
    /// <summary>
    /// The auction is currently accepting valid bids.
    /// </summary>
    Open = 1,
    /// <summary>
    /// The auction has ended and no longer accepts bids.
    /// </summary>
    Closed = 2,
    /// <summary>
    /// The auction was cancelled before completion.
    /// </summary>
    Cancelled = 3
}
