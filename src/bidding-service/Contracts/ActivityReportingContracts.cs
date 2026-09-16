// <copyright file="ActivityReportingContracts.cs" company="Distributed Bidding Auction Platform">
// Copyright (c) Distributed Bidding Auction Platform. Licensed under the MIT license.
// </copyright>
namespace bidding_service.Contracts;

/// <summary>Represents one daily activity count.</summary>
public sealed record DailyActivityCount(string Date, long Count);

/// <summary>Represents tenant-scoped activity counts for a UTC date window.</summary>
public sealed record ActivityReportResponse(
    string From,
    string To,
    int Days,
    IReadOnlyList<DailyActivityCount> Bids,
    IReadOnlyList<DailyActivityCount> Purchases);
