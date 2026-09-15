// <copyright file="BidRules.cs" company="Distributed Bidding Auction Platform">
// Copyright (c) Distributed Bidding Auction Platform. Licensed under the MIT license.
// </copyright>
using bidding_service.Domain;

namespace bidding_service.Services;

/// <summary>
/// Provides shared bid rule calculations for authoritative auction state.
/// </summary>
public static class BidRules
{
    /// <summary>
    /// Calculates the minimum valid bid from authoritative auction state.
    /// </summary>
    public static decimal GetMinimumValidBid(Auction auction)
    {
        return auction.CurrentBidAmount is null
            ? auction.StartingPrice
            : auction.CurrentBidAmount.Value + auction.MinimumBidIncrement;
    }
}
