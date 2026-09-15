// <copyright file="BiddingServiceDatabaseCollection.cs" company="Distributed Bidding Auction Platform">
// Copyright (c) Distributed Bidding Auction Platform. Licensed under the MIT license.
// </copyright>
namespace bidding_service.Tests;

/// <summary>
/// Serializes tests that reset the shared PostgreSQL bidding-service test database.
/// </summary>
[CollectionDefinition("Bidding service database", DisableParallelization = true)]
public sealed class BiddingServiceDatabaseTestGroup;
