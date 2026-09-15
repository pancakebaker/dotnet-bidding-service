// <copyright file="SchedulerOptions.cs" company="Distributed Bidding Auction Platform">
// Copyright (c) Distributed Bidding Auction Platform. Licensed under the MIT license.
// </copyright>
namespace auction_scheduler.Options;

/// <summary>
/// Configures auction scheduler polling behavior.
/// </summary>
public sealed class SchedulerOptions
{
    /// <summary>
    /// Identifies the configuration section name.
    /// </summary>
    public const string SectionName = "Scheduler";

    /// <summary>
    /// Gets or sets the polling interval in seconds.
    /// </summary>
    public int PollIntervalSeconds { get; init; } = 1;
    /// <summary>
    /// Gets or sets the maximum number of records processed per pass.
    /// </summary>
    public int BatchSize { get; init; } = 20;

    /// <summary>
    /// Gets the validated scheduler polling interval.
    /// </summary>
    public TimeSpan PollInterval => TimeSpan.FromSeconds(Math.Max(1, PollIntervalSeconds));
    /// <summary>
    /// Gets the validated scheduler batch size.
    /// </summary>
    public int EffectiveBatchSize => Math.Clamp(BatchSize, 1, 100);
}
