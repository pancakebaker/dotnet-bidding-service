// <copyright file="AuctionClosingService.cs" company="Distributed Bidding Auction Platform">
// Copyright (c) Distributed Bidding Auction Platform. Licensed under the MIT license.
// </copyright>
using System.Data;
using System.Text.Json;
using auction_scheduler.Outbox;
using DistributedBidding.IntegrationContracts;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace auction_scheduler;

/// <summary>
/// Closes expired open auctions and records lifecycle outbox events.
/// </summary>
public sealed class AuctionClosingService(
    NpgsqlDataSource dataSource,
    TimeProvider timeProvider,
    IOptions<Options.SchedulerOptions> options,
    ILogger<AuctionClosingService> logger)
{
    private const string OpenStatus = "Open";
    private const string ClosedStatus = "Closed";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Closes a bounded batch of expired open auctions.
    /// </summary>
    public async Task<int> CloseExpiredAuctionsAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var closedCount = 0;

        for (var index = 0; index < options.Value.EffectiveBatchSize; index++)
        {
            var closed = await CloseNextExpiredAuctionAsync(now, cancellationToken);
            if (!closed)
            {
                break;
            }

            closedCount++;
        }

        if (closedCount > 0)
        {
            logger.LogInformation("Closed {AuctionCount} expired auctions.", closedCount);
        }

        return closedCount;
    }

    /// <summary>
    /// Claims and closes one expired open auction in a PostgreSQL transaction.
    /// </summary>
    public async Task<bool> CloseNextExpiredAuctionAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        try
        {
            var auction = await ClaimExpiredOpenAuctionAsync(
                connection,
                transaction,
                now,
                cancellationToken);
            if (auction is null)
            {
                await transaction.CommitAsync(cancellationToken);
                return false;
            }

            var winner = await FindWinningBidAsync(
                connection,
                transaction,
                auction,
                cancellationToken);
            var newVersion = await CloseAuctionAsync(
                connection,
                transaction,
                auction,
                winner,
                now,
                cancellationToken);
            if (newVersion is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                logger.LogInformation(
                    "Auction close skipped after version/status conflict. AuctionId: {AuctionId}, ExpectedVersion: {AuctionVersion}",
                    auction.Id,
                    auction.Version);
                return false;
            }

            var correlationId = Guid.NewGuid().ToString();
            await InsertAuctionClosedOutboxAsync(
                connection,
                transaction,
                auction,
                newVersion.Value,
                now,
                correlationId,
                cancellationToken);

            if (winner is not null)
            {
                await InsertWinnerSelectedOutboxAsync(
                    connection,
                    transaction,
                    winner,
                    auction.TenantId,
                    auction.Id,
                    newVersion.Value,
                    now,
                    correlationId,
                    cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);

            logger.LogInformation(
                "Closed auction {AuctionId}. Version advanced from {OldVersion} to {NewVersion}. WinnerBidId: {WinningBidId}. CorrelationId: {CorrelationId}",
                auction.Id,
                auction.Version,
                newVersion.Value,
                winner?.Id,
                correlationId);

            return true;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task<ClaimedAuction?> ClaimExpiredOpenAuctionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT id, tenant_id, current_bid_amount, current_bidder_id, version, end_time_utc
            FROM auctions
            WHERE status = @openStatus
              AND end_time_utc <= @now
            ORDER BY end_time_utc, id
            LIMIT 1
            FOR UPDATE SKIP LOCKED;
            """,
            connection,
            transaction);

        command.Parameters.AddWithValue("openStatus", OpenStatus);
        command.Parameters.AddWithValue("now", now);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ClaimedAuction(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.IsDBNull(2) ? null : reader.GetDecimal(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetInt64(4),
            reader.GetFieldValue<DateTimeOffset>(5));
    }

    private static async Task<WinningBid?> FindWinningBidAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ClaimedAuction auction,
        CancellationToken cancellationToken)
    {
        if (auction.CurrentBidAmount is null || string.IsNullOrWhiteSpace(auction.CurrentBidderId))
        {
            return null;
        }

        await using var command = new NpgsqlCommand(
            """
            SELECT id, bidder_id, amount
            FROM bids
            WHERE auction_id = @auctionId
              AND bidder_id = @bidderId
              AND amount = @amount
            ORDER BY created_at_utc DESC, id DESC
            LIMIT 1;
            """,
            connection,
            transaction);

        command.Parameters.AddWithValue("auctionId", auction.Id);
        command.Parameters.AddWithValue("bidderId", auction.CurrentBidderId);
        command.Parameters.AddWithValue("amount", auction.CurrentBidAmount.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new WinningBid(reader.GetGuid(0), reader.GetString(1), reader.GetDecimal(2));
    }

    private static async Task<long?> CloseAuctionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ClaimedAuction auction,
        WinningBid? winner,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            UPDATE auctions
            SET status = @closedStatus,
                final_winner_id = @finalWinnerId,
                final_price = @finalPrice,
                version = version + 1,
                updated_at_utc = @updatedAtUtc
            WHERE id = @auctionId
              AND status = @openStatus
              AND version = @expectedVersion
            RETURNING version;
            """,
            connection,
            transaction);

        command.Parameters.AddWithValue("closedStatus", ClosedStatus);
        command.Parameters.Add("finalWinnerId", NpgsqlDbType.Text).Value =
            winner?.BidderId ?? (object)DBNull.Value;
        command.Parameters.Add("finalPrice", NpgsqlDbType.Numeric).Value =
            winner?.Amount ?? (object)DBNull.Value;
        command.Parameters.AddWithValue("updatedAtUtc", now);
        command.Parameters.AddWithValue("auctionId", auction.Id);
        command.Parameters.AddWithValue("openStatus", OpenStatus);
        command.Parameters.AddWithValue("expectedVersion", auction.Version);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null ? null : (long)result;
    }

    private static Task InsertAuctionClosedOutboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ClaimedAuction auction,
        long auctionVersion,
        DateTimeOffset occurredAtUtc,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var payload = new AuctionClosedPayload(
            auction.TenantId,
            auction.Id,
            occurredAtUtc,
            auction.CurrentBidAmount,
            auction.CurrentBidderId,
            auctionVersion);

        return InsertOutboxMessageAsync(
            connection,
            transaction,
            IntegrationEventTypes.AuctionClosed,
            auction.Id,
            auctionVersion,
            occurredAtUtc,
            correlationId,
            payload,
            cancellationToken);
    }

    private static Task InsertWinnerSelectedOutboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        WinningBid winner,
        Guid tenantId,
        Guid auctionId,
        long auctionVersion,
        DateTimeOffset occurredAtUtc,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var payload = new WinnerSelectedPayload(
            tenantId,
            auctionId,
            winner.Id,
            winner.BidderId,
            winner.Amount,
            occurredAtUtc,
            auctionVersion);

        return InsertOutboxMessageAsync(
            connection,
            transaction,
            IntegrationEventTypes.WinnerSelected,
            auctionId,
            auctionVersion,
            occurredAtUtc,
            correlationId,
            payload,
            cancellationToken);
    }

    private static async Task InsertOutboxMessageAsync<TPayload>(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string eventType,
        Guid aggregateId,
        long aggregateVersion,
        DateTimeOffset occurredAtUtc,
        string correlationId,
        TPayload payload,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO outbox_messages
                (id, event_type, aggregate_type, aggregate_id, aggregate_version, occurred_at_utc,
                 correlation_id, payload, created_at_utc, publish_attempts)
            VALUES
                (@id, @eventType, @aggregateType, @aggregateId, @aggregateVersion, @occurredAtUtc,
                 @correlationId, @payload::jsonb, @createdAtUtc, 0);
            """,
            connection,
            transaction);

        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("eventType", eventType);
        command.Parameters.AddWithValue("aggregateType", AggregateTypes.Auction);
        command.Parameters.AddWithValue("aggregateId", aggregateId);
        command.Parameters.AddWithValue("aggregateVersion", aggregateVersion);
        command.Parameters.AddWithValue("occurredAtUtc", occurredAtUtc);
        command.Parameters.AddWithValue("correlationId", correlationId);
        command.Parameters.AddWithValue(
            "payload",
            NpgsqlDbType.Jsonb,
            JsonSerializer.Serialize(payload, JsonOptions));
        command.Parameters.AddWithValue("createdAtUtc", occurredAtUtc);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed record ClaimedAuction(
        Guid Id,
        Guid TenantId,
        decimal? CurrentBidAmount,
        string? CurrentBidderId,
        long Version,
        DateTimeOffset EndTimeUtc);

    private sealed record WinningBid(Guid Id, string BidderId, decimal Amount);
}
