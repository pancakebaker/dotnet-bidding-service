// <copyright file="Worker.cs" company="Distributed Bidding Auction Platform">
// Copyright (c) Distributed Bidding Auction Platform. Licensed under the MIT license.
// </copyright>
using Microsoft.Extensions.Options;

namespace auction_scheduler;

/// <summary>
/// Runs the background worker loop for this service.
/// </summary>
public sealed class Worker(
    AuctionClosingService closingService,
    IOptions<Options.SchedulerOptions> options,
    ILogger<Worker> logger) : BackgroundService
{
    /// <summary>
    /// Runs the hosted background processing loop.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Auction Scheduler started. PollInterval: {PollInterval}, BatchSize: {BatchSize}",
            options.Value.PollInterval,
            options.Value.EffectiveBatchSize);

        using var timer = new PeriodicTimer(options.Value.PollInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceAsync(stoppingToken);

            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            await closingService.CloseExpiredAuctionsAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Auction scheduler pass failed.");
        }
    }
}
