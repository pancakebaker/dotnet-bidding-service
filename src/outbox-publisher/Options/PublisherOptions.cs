// <copyright file="PublisherOptions.cs" company="Distributed Bidding Auction Platform">
// Copyright (c) Distributed Bidding Auction Platform. Licensed under the MIT license.
// </copyright>
namespace outbox_publisher.Options;

/// <summary>
/// Configures outbox publisher polling and retry behavior.
/// </summary>
public sealed class PublisherOptions
{
    /// <summary>
    /// Identifies the configuration section name.
    /// </summary>
    public const string SectionName = "Publisher";
    /// <summary>
    /// Gets or sets the polling interval in seconds.
    /// </summary>
    public int PollIntervalSeconds { get; set; } = 1;
    /// <summary>
    /// Gets or sets the maximum number of records processed per pass.
    /// </summary>
    public int BatchSize { get; set; } = 20;
    /// <summary>
    /// Gets or sets the maximum publish attempts allowed before a row is skipped.
    /// </summary>
    public int MaxPublishAttempts { get; set; } = 10;
}
