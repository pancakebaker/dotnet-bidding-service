// <copyright file="IntegrationEventRoutingKeys.cs" company="Distributed Bidding Auction Platform">
// Copyright (c) Distributed Bidding Auction Platform. Licensed under the MIT license.
// </copyright>
namespace DistributedBidding.IntegrationContracts;

/// <summary>Defines routing keys for the stable auction integration events.</summary>
public static class IntegrationEventRoutingKeys
{
    /// <summary>Routes accepted bid events.</summary>
    public const string BidAccepted = "auction.bid.accepted";

    /// <summary>Routes auction-closed events.</summary>
    public const string AuctionClosed = "auction.closed";

    /// <summary>Routes winner-selected events.</summary>
    public const string WinnerSelected = "auction.winner.selected";

    /// <summary>Routes explicit Buy Now purchase events.</summary>
    public const string AuctionPurchased = "auction.purchased";

    /// <summary>Routes explicit auction cancellation events.</summary>
    public const string AuctionCancelled = "auction.cancelled";

    /// <summary>Routes tenant status transitions.</summary>
    public const string TenantStatusChanged = "tenant.status.changed";
}
