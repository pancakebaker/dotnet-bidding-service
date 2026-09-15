// <copyright file="IntegrationEventTypes.cs" company="Distributed Bidding Auction Platform">
// Copyright (c) Distributed Bidding Auction Platform. Licensed under the MIT license.
// </copyright>
namespace DistributedBidding.IntegrationContracts;

/// <summary>Defines event discriminator values shared by auction services.</summary>
public static class IntegrationEventTypes
{
    /// <summary>Identifies an accepted bid event.</summary>
    public const string BidAccepted = "BidAccepted";

    /// <summary>Identifies an auction-closed event.</summary>
    public const string AuctionClosed = "AuctionClosed";

    /// <summary>Identifies a winner-selected event.</summary>
    public const string WinnerSelected = "WinnerSelected";

    /// <summary>Identifies an explicit Buy Now purchase event.</summary>
    public const string AuctionPurchased = "AuctionPurchased";

    /// <summary>Identifies an explicit auction cancellation event.</summary>
    public const string AuctionCancelled = "AuctionCancelled";

    /// <summary>Identifies a committed tenant status transition.</summary>
    public const string TenantStatusChanged = "TenantStatusChanged";
}
