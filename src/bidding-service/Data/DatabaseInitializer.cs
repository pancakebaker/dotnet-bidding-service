// <copyright file="DatabaseInitializer.cs" company="Distributed Bidding Auction Platform">
// Copyright (c) Distributed Bidding Auction Platform. Licensed under the MIT license.
// </copyright>
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace bidding_service.Data;

/// <summary>
/// Applies local database setup tasks for the bidding service.
/// </summary>
public sealed class DatabaseInitializer(
    IServiceScopeFactory scopeFactory,
    IOptions<DatabaseOptions> options,
    TimeProvider timeProvider,
    ILogger<DatabaseInitializer> logger)
{
    /// <summary>
    /// Initializes the bidding database for local development and demo runs.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BiddingDbContext>();

        if (options.Value.ApplyMigrations)
        {
            await db.Database.MigrateAsync(cancellationToken);
        }

        if (options.Value.SeedDemoData)
        {
            await DatabaseSeeder.SeedAsync(db, timeProvider, cancellationToken);
            logger.LogInformation("Demo auction seed data is ready.");
        }
    }
}
