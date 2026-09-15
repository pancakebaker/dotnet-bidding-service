// <copyright file="BidPlacementOptions.cs" company="Distributed Bidding Auction Platform">
// Copyright (c) Distributed Bidding Auction Platform. Licensed under the MIT license.
// </copyright>
namespace bidding_service.Services;

/// <summary>
/// Configures bid placement behavior used by concurrency tests and local development.
/// </summary>
public sealed class BidPlacementOptions
{
    /// <summary>
    /// Identifies the configuration section name.
    /// </summary>
    public const string SectionName = "BidPlacement";

    /// <summary>
    /// Gets or sets the bounded optimistic concurrency retry count for bid placement.
    /// </summary>
    public int MaxConcurrencyRetries { get; set; } = 2;

    /// <summary>
    /// Gets or sets an optional artificial delay used by concurrency tests.
    /// </summary>
    public int ArtificialProcessingDelayMilliseconds { get; set; } = 0;
}
