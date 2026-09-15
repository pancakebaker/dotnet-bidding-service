// <copyright file="LifecycleEventPayloads.cs" company="Distributed Bidding Auction Platform">
// Copyright (c) Distributed Bidding Auction Platform. Licensed under the MIT license.
// </copyright>
namespace auction_scheduler.Outbox;

/// <summary>
/// Represents the AuctionClosed lifecycle event payload.
/// </summary>
public sealed record AuctionClosedPayload(
    Guid TenantId,
    Guid AuctionId,
    DateTimeOffset ClosedAtUtc,
    decimal? FinalBidAmount,
    string? FinalBidderId,
    long AuctionVersion);

/// <summary>
/// Represents the WinnerSelected lifecycle event payload.
/// </summary>
public sealed record WinnerSelectedPayload(
    Guid TenantId,
    Guid AuctionId,
    Guid WinningBidId,
    string WinnerId,
    decimal Amount,
    DateTimeOffset SelectedAtUtc,
    long AuctionVersion);
