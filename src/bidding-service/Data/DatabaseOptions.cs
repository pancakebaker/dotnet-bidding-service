// <copyright file="DatabaseOptions.cs" company="Distributed Bidding Auction Platform">
// Copyright (c) Distributed Bidding Auction Platform. Licensed under the MIT license.
// </copyright>
namespace bidding_service.Data;

/// <summary>
/// Configures local database migration and seed behavior.
/// </summary>
public sealed class DatabaseOptions
{
    /// <summary>
    /// Identifies the configuration section name.
    /// </summary>
    public const string SectionName = "Database";

    /// <summary>
    /// Gets or sets a value indicating whether the service applies EF Core migrations at startup.
    /// </summary>
    public bool ApplyMigrations { get; set; } = true;
    /// <summary>
    /// Gets or sets a value indicating whether deterministic demo auctions are seeded at startup.
    /// </summary>
    public bool SeedDemoData { get; set; } = true;
}
