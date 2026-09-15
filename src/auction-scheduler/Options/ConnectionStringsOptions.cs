// <copyright file="ConnectionStringsOptions.cs" company="Distributed Bidding Auction Platform">
// Copyright (c) Distributed Bidding Auction Platform. Licensed under the MIT license.
// </copyright>
namespace auction_scheduler.Options;

/// <summary>
/// Configures database connection strings for the worker.
/// </summary>
public sealed class ConnectionStringsOptions
{
    /// <summary>
    /// Identifies the configuration section name.
    /// </summary>
    public const string SectionName = "ConnectionStrings";

    /// <summary>
    /// Gets or sets the bidding database connection string.
    /// </summary>
    public string BiddingDb { get; init; } = string.Empty;
}
