// <copyright file="AggregateTypes.cs" company="Distributed Bidding Auction Platform">
// Copyright (c) Distributed Bidding Auction Platform. Licensed under the MIT license.
// </copyright>
namespace DistributedBidding.IntegrationContracts;

/// <summary>Defines aggregate discriminator values shared by auction services.</summary>
public static class AggregateTypes
{
    /// <summary>Identifies the auction aggregate.</summary>
    public const string Auction = "Auction";

    /// <summary>Identifies the tenant aggregate.</summary>
    public const string Tenant = "Tenant";
}
