// <copyright file="TenantStatusChangedPayload.cs" company="Distributed Bidding Auction Platform">
// Copyright (c) Distributed Bidding Auction Platform. Licensed under the MIT license.
// </copyright>
namespace DistributedBidding.IntegrationContracts;

/// <summary>Represents a committed, ordered tenant lifecycle transition.</summary>
public sealed record TenantStatusChangedPayload(
    Guid EventId,
    Guid TenantId,
    string PreviousStatus,
    string CurrentStatus,
    long TenantVersion,
    DateTimeOffset OccurredAtUtc);
