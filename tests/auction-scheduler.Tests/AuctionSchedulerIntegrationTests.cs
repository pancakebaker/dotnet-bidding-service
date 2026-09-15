using System.Globalization;
using System.Text.Json;
using auction_scheduler.Options;
using bidding_service.Data;
using bidding_service.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace auction_scheduler.Tests;

public sealed class AuctionSchedulerIntegrationTests : IAsyncLifetime
{
    private const string DatabaseName = "auction_demo_scheduler_tests";
    private const string ConnectionString = "Host=127.0.0.1;Port=55432;Database=auction_demo_scheduler_tests;Username=auction_app;Password=change_me_in_local_env";
    private const string MaintenanceConnectionString = "Host=127.0.0.1;Port=55432;Database=postgres;Username=auction_app;Password=change_me_in_local_env";

    public async Task InitializeAsync() => await ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ExpiredOpenAuctionWithoutBids_ClosesAndCreatesOnlyAuctionClosedEvent()
    {
        var auctionId = Guid.NewGuid();
        await InsertAuctionAsync(auctionId, status: "Open", version: 7, endOffsetMinutes: -5);
        var service = CreateService();

        Assert.Equal(1, await service.CloseExpiredAuctionsAsync(CancellationToken.None));
        Assert.Equal(0, await service.CloseExpiredAuctionsAsync(CancellationToken.None));

        var auction = await GetAuctionAsync(auctionId);
        var events = await GetOutboxMessagesAsync(auctionId);

        Assert.Equal("Closed", auction.Status);
        Assert.Equal("AuctionOnly", auction.SaleMode);
        Assert.Equal(8, auction.Version);
        Assert.Null(auction.FinalWinnerId);
        Assert.Null(auction.FinalPrice);
        var closed = Assert.Single(events);
        Assert.Equal("AuctionClosed", closed.EventType);
        Assert.Equal(8, closed.AggregateVersion);

        using var document = JsonDocument.Parse(closed.Payload);
        Assert.Equal(auctionId, document.RootElement.GetProperty("auctionId").GetGuid());
        Assert.True(document.RootElement.GetProperty("finalBidAmount").ValueKind is JsonValueKind.Null);
        Assert.True(document.RootElement.GetProperty("finalBidderId").ValueKind is JsonValueKind.Null);
        Assert.Equal(8, document.RootElement.GetProperty("auctionVersion").GetInt64());
    }

    [Fact]
    public async Task ExpiredOpenAuctionWithBids_ClosesAndCreatesWinnerSelectedWithSameVersionAndCorrelation()
    {
        var auctionId = Guid.NewGuid();
        var winningBidId = Guid.NewGuid();
        await InsertAuctionAsync(auctionId, status: "Open", version: 15, endOffsetMinutes: -2, currentBidAmount: 12500m, currentBidderId: "alice");
        await InsertBidAsync(Guid.NewGuid(), auctionId, "bob", 12000m, createdOffsetSeconds: -20);
        await InsertBidAsync(winningBidId, auctionId, "alice", 12500m, createdOffsetSeconds: -5);
        var service = CreateService();

        Assert.Equal(1, await service.CloseExpiredAuctionsAsync(CancellationToken.None));

        var auction = await GetAuctionAsync(auctionId);
        var events = await GetOutboxMessagesAsync(auctionId);
        var closed = Assert.Single(events, message => message.EventType == "AuctionClosed");
        var winner = Assert.Single(events, message => message.EventType == "WinnerSelected");

        Assert.Equal("Closed", auction.Status);
        Assert.Equal("AuctionOnly", auction.SaleMode);
        Assert.Equal(16, auction.Version);
        Assert.Equal("alice", auction.FinalWinnerId);
        Assert.Equal(12500m, auction.FinalPrice);
        Assert.Equal(16, closed.AggregateVersion);
        Assert.Equal(16, winner.AggregateVersion);
        Assert.Equal(closed.CorrelationId, winner.CorrelationId);
        Assert.False(string.IsNullOrWhiteSpace(closed.CorrelationId));

        using var winnerPayload = JsonDocument.Parse(winner.Payload);
        Assert.Equal(winningBidId, winnerPayload.RootElement.GetProperty("winningBidId").GetGuid());
        Assert.Equal("alice", winnerPayload.RootElement.GetProperty("winnerId").GetString());
        Assert.Equal(12500m, winnerPayload.RootElement.GetProperty("amount").GetDecimal());
        Assert.Equal(16, winnerPayload.RootElement.GetProperty("auctionVersion").GetInt64());
    }

    [Fact]
    public async Task PurchasedAuctionPastEnd_IsIgnoredByScheduler()
    {
        var auctionId = Guid.NewGuid();
        await InsertAuctionAsync(
            auctionId,
            status: "Closed",
            version: 4,
            endOffsetMinutes: -30,
            saleMode: "BuyNowOnly",
            buyNowPrice: 15000m,
            finalWinnerId: "buyer-123",
            finalPrice: 15000m);
        await InsertOutboxEventAsync(auctionId, "AuctionPurchased", 4);
        await InsertOutboxEventAsync(auctionId, "AuctionClosed", 4);
        var service = CreateService();

        Assert.Equal(0, await service.CloseExpiredAuctionsAsync(CancellationToken.None));

        var auction = await GetAuctionAsync(auctionId);
        var events = await GetOutboxMessagesAsync(auctionId);
        Assert.Equal("Closed", auction.Status);
        Assert.Equal(4, auction.Version);
        Assert.Equal("buyer-123", auction.FinalWinnerId);
        Assert.Equal(15000m, auction.FinalPrice);
        Assert.Equal(2, events.Count);
        Assert.DoesNotContain(events, message => message.EventType == "WinnerSelected");
    }

    [Fact]
    public async Task ExpiredAuctionAndBuyNowWithBid_ClosesAsOrdinaryAuction()
    {
        var auctionId = Guid.NewGuid();
        await InsertAuctionAsync(
            auctionId,
            status: "Open",
            version: 8,
            endOffsetMinutes: -5,
            saleMode: "AuctionAndBuyNow",
            buyNowPrice: 15000m,
            currentBidAmount: 12500m,
            currentBidderId: "alice");
        await InsertBidAsync(Guid.NewGuid(), auctionId, "alice", 12500m, createdOffsetSeconds: -1);
        var service = CreateService();

        Assert.Equal(1, await service.CloseExpiredAuctionsAsync(CancellationToken.None));

        var auction = await GetAuctionAsync(auctionId);
        var events = await GetOutboxMessagesAsync(auctionId);
        Assert.Equal("Closed", auction.Status);
        Assert.Equal(9, auction.Version);
        Assert.Equal("alice", auction.FinalWinnerId);
        Assert.Equal(12500m, auction.FinalPrice);
        Assert.Single(events, message => message.EventType == "AuctionClosed");
        Assert.Single(events, message => message.EventType == "WinnerSelected");
        Assert.DoesNotContain(events, message => message.EventType == "AuctionPurchased");
    }

    [Theory]
    [InlineData("AuctionAndBuyNow")]
    [InlineData("BuyNowOnly")]
    public async Task ExpiredBuyNowCapableAuctionWithoutPurchase_ClosesWithoutWinner(string saleMode)
    {
        var auctionId = Guid.NewGuid();
        await InsertAuctionAsync(
            auctionId,
            status: "Open",
            version: 11,
            endOffsetMinutes: -5,
            saleMode: saleMode,
            buyNowPrice: 15000m);
        var service = CreateService();

        Assert.Equal(1, await service.CloseExpiredAuctionsAsync(CancellationToken.None));

        var auction = await GetAuctionAsync(auctionId);
        var events = await GetOutboxMessagesAsync(auctionId);
        Assert.Equal("Closed", auction.Status);
        Assert.Equal(12, auction.Version);
        Assert.Null(auction.FinalWinnerId);
        Assert.Null(auction.FinalPrice);
        Assert.Single(events, message => message.EventType == "AuctionClosed");
        Assert.DoesNotContain(events, message => message.EventType is "WinnerSelected" or "AuctionPurchased");
    }

    [Fact]
    public async Task NonEligibleAuctions_DoNotClose()
    {
        var futureOpen = Guid.NewGuid();
        var scheduledExpired = Guid.NewGuid();
        var closedExpired = Guid.NewGuid();
        var cancelledExpired = Guid.NewGuid();

        await InsertAuctionAsync(futureOpen, status: "Open", version: 1, endOffsetMinutes: 30);
        await InsertAuctionAsync(scheduledExpired, status: "Scheduled", version: 1, endOffsetMinutes: -30);
        await InsertAuctionAsync(closedExpired, status: "Closed", version: 1, endOffsetMinutes: -30);
        await InsertAuctionAsync(cancelledExpired, status: "Cancelled", version: 1, endOffsetMinutes: -30);

        var service = CreateService();

        Assert.Equal(0, await service.CloseExpiredAuctionsAsync(CancellationToken.None));
        Assert.Equal("Open", (await GetAuctionAsync(futureOpen)).Status);
        Assert.Equal("Scheduled", (await GetAuctionAsync(scheduledExpired)).Status);
        Assert.Equal("Closed", (await GetAuctionAsync(closedExpired)).Status);
        Assert.Equal("Cancelled", (await GetAuctionAsync(cancelledExpired)).Status);
        Assert.Equal(0, await GetOutboxCountAsync());
    }

    [Fact]
    public async Task TwoSchedulerInstances_DoNotDoubleCloseTheSameAuction()
    {
        var auctionId = Guid.NewGuid();
        await InsertAuctionAsync(auctionId, status: "Open", version: 3, endOffsetMinutes: -1);
        var first = CreateService();
        var second = CreateService();

        var results = await Task.WhenAll(
            first.CloseExpiredAuctionsAsync(CancellationToken.None),
            second.CloseExpiredAuctionsAsync(CancellationToken.None));

        var auction = await GetAuctionAsync(auctionId);
        var events = await GetOutboxMessagesAsync(auctionId);

        Assert.Equal(1, results.Sum());
        Assert.Equal("Closed", auction.Status);
        Assert.Equal(4, auction.Version);
        Assert.Single(events, message => message.EventType == "AuctionClosed");
    }

    [Fact]
    public async Task BidCommittedBeforeClose_IsPreservedAsWinnerDuringClosure()
    {
        var auctionId = Guid.NewGuid();
        var bidId = Guid.NewGuid();
        await InsertAuctionAsync(auctionId, status: "Open", version: 1, endOffsetMinutes: -1);
        await InsertBidAsync(bidId, auctionId, "charlie", 15000m, createdOffsetSeconds: -1);
        await SetCurrentBidAsync(auctionId, "charlie", 15000m, version: 2);
        var service = CreateService();

        Assert.Equal(1, await service.CloseExpiredAuctionsAsync(CancellationToken.None));

        var auction = await GetAuctionAsync(auctionId);
        var winner = Assert.Single(await GetOutboxMessagesAsync(auctionId), message => message.EventType == "WinnerSelected");

        Assert.Equal("Closed", auction.Status);
        Assert.Equal(3, auction.Version);
        Assert.Equal("charlie", auction.FinalWinnerId);
        Assert.Equal(15000m, auction.FinalPrice);
        using var document = JsonDocument.Parse(winner.Payload);
        Assert.Equal(bidId, document.RootElement.GetProperty("winningBidId").GetGuid());
        Assert.Equal("charlie", document.RootElement.GetProperty("winnerId").GetString());
    }

    [Fact]
    public async Task CloseFailureRollsBackAuctionAndLifecycleEvents()
    {
        var auctionId = Guid.NewGuid();
        await InsertAuctionAsync(auctionId, status: "Open", version: 9, endOffsetMinutes: -1);
        await DropOutboxTableAsync();
        var service = CreateService();

        await Assert.ThrowsAsync<PostgresException>(() => service.CloseExpiredAuctionsAsync(CancellationToken.None));

        await ReapplyOutboxMigrationAsync();
        var auction = await GetAuctionAsync(auctionId);
        Assert.Equal("Open", auction.Status);
        Assert.Equal(9, auction.Version);
        Assert.Equal(0, await GetOutboxCountAsync());
    }

    [Fact]
    public async Task BiddingSchema_IsCompatibleWithSchedulerSql()
    {
        await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
        await using var connection = await dataSource.OpenConnectionAsync(CancellationToken.None);

        await AssertColumnsAsync(connection, "auctions", new Dictionary<string, ColumnExpectation>
        {
            ["id"] = new("uuid", false),
            ["status"] = new("string", false),
            ["end_time_utc"] = new("timestamptz", false),
            ["current_bid_amount"] = new("numeric", true),
            ["current_bidder_id"] = new("string", true),
            ["version"] = new("bigint", false),
            ["updated_at_utc"] = new("timestamptz", false)
        });
        await AssertColumnsAsync(connection, "bids", new Dictionary<string, ColumnExpectation>
        {
            ["id"] = new("uuid", false),
            ["auction_id"] = new("uuid", false),
            ["bidder_id"] = new("string", false),
            ["amount"] = new("numeric", false),
            ["created_at_utc"] = new("timestamptz", false)
        });
        await AssertColumnsAsync(connection, "outbox_messages", new Dictionary<string, ColumnExpectation>
        {
            ["id"] = new("uuid", false),
            ["event_type"] = new("string", false),
            ["aggregate_type"] = new("string", false),
            ["aggregate_id"] = new("uuid", false),
            ["aggregate_version"] = new("bigint", false),
            ["occurred_at_utc"] = new("timestamptz", false),
            ["correlation_id"] = new("string", true),
            ["payload"] = new("jsonb", false),
            ["created_at_utc"] = new("timestamptz", false),
            ["publish_attempts"] = new("integer", false)
        });

        await AssertIndexExistsAsync(connection, "auctions", "ix_auctions_status_end_time_utc");
        await AssertIndexExistsAsync(connection, "bids", "ix_bids_auction_id_created_at_utc");
    }

    private static AuctionClosingService CreateService()
    {
        var dataSource = NpgsqlDataSource.Create(ConnectionString);
        return new AuctionClosingService(
            dataSource,
            TimeProvider.System,
            Microsoft.Extensions.Options.Options.Create(new SchedulerOptions { BatchSize = 20, PollIntervalSeconds = 1 }),
            NullLogger<AuctionClosingService>.Instance);
    }

    private static async Task ResetDatabaseAsync()
    {
        await EnsureTestDatabaseExistsAsync();
        await using var db = CreateDbContext();
        await db.Database.EnsureDeletedAsync(CancellationToken.None);
        await db.Database.MigrateAsync(CancellationToken.None);
    }

    private static async Task EnsureTestDatabaseExistsAsync()
    {
        await using var dataSource = NpgsqlDataSource.Create(MaintenanceConnectionString);
        await using var connection = await dataSource.OpenConnectionAsync(CancellationToken.None);
        await using var existsCommand = new NpgsqlCommand("select 1 from pg_database where datname = @databaseName", connection);
        existsCommand.Parameters.AddWithValue("databaseName", DatabaseName);
        var exists = await existsCommand.ExecuteScalarAsync(CancellationToken.None);
        if (exists is not null)
        {
            return;
        }

        await using var createCommand = new NpgsqlCommand($"CREATE DATABASE {DatabaseName}", connection);
        await createCommand.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static BiddingDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<BiddingDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;
        return new BiddingDbContext(options);
    }

    private static async Task ReapplyOutboxMigrationAsync()
    {
        await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
        await using var connection = await dataSource.OpenConnectionAsync(CancellationToken.None);
        await using var command = new NpgsqlCommand(
            "DELETE FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = '20260904194837_AddTransactionalOutbox';",
            connection);
        await command.ExecuteNonQueryAsync(CancellationToken.None);

        await using var db = CreateDbContext();
        await db.Database.MigrateAsync(CancellationToken.None);
    }

    private static async Task AssertColumnsAsync(
        NpgsqlConnection connection,
        string tableName,
        IReadOnlyDictionary<string, ColumnExpectation> expectations)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT column_name, data_type, udt_name, is_nullable
            FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = @tableName;
            """,
            connection);
        command.Parameters.AddWithValue("tableName", tableName);

        var columns = new Dictionary<string, ColumnMetadata>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        while (await reader.ReadAsync(CancellationToken.None))
        {
            columns[reader.GetString(0)] = new(
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3) == "YES");
        }

        foreach (var (columnName, expectation) in expectations)
        {
            Assert.True(
                columns.TryGetValue(columnName, out var actual),
                $"Expected {tableName}.{columnName} to exist. Available columns: {string.Join(", ", columns.Keys)}");
            Assert.True(
                expectation.TypeCategory == GetTypeCategory(actual!),
                $"Unexpected type for {tableName}.{columnName}: expected={expectation.TypeCategory}, data_type={actual!.DataType}, udt_name={actual.UdtName}");
            Assert.True(
                expectation.Nullable == actual.IsNullable,
                $"Unexpected nullability for {tableName}.{columnName}: expected_nullable={expectation.Nullable}, actual_nullable={actual.IsNullable}");
        }
    }

    private static async Task AssertIndexExistsAsync(
        NpgsqlConnection connection,
        string tableName,
        string indexName)
    {
        await using var command = new NpgsqlCommand(
            "SELECT 1 FROM pg_indexes WHERE schemaname = 'public' AND tablename = @tableName AND indexname = @indexName;",
            connection);
        command.Parameters.AddWithValue("tableName", tableName);
        command.Parameters.AddWithValue("indexName", indexName);
        var exists = await command.ExecuteScalarAsync(CancellationToken.None) is not null;
        Assert.True(exists, $"Expected index {indexName} on {tableName} to exist in the migrated schema.");
    }

    private static string GetTypeCategory(ColumnMetadata metadata)
    {
        if (metadata.UdtName == "uuid")
        {
            return "uuid";
        }

        if (metadata.UdtName == "jsonb")
        {
            return "jsonb";
        }

        if (metadata.DataType is "character varying" or "character" or "text")
        {
            return "string";
        }

        return metadata.DataType switch
        {
            "timestamp with time zone" => "timestamptz",
            "numeric" => "numeric",
            "bigint" => "bigint",
            "integer" => "integer",
            _ => metadata.DataType
        };
    }

    private static async Task DropOutboxTableAsync()
    {
        await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
        await using var connection = await dataSource.OpenConnectionAsync(CancellationToken.None);
        await using var command = new NpgsqlCommand("DROP TABLE outbox_messages;", connection);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static async Task InsertAuctionAsync(
        Guid auctionId,
        string status,
        long version,
        int endOffsetMinutes,
        decimal? currentBidAmount = null,
        string? currentBidderId = null,
        string saleMode = "AuctionOnly",
        decimal? buyNowPrice = null,
        string? finalWinnerId = null,
        decimal? finalPrice = null)
    {
        var now = DateTimeOffset.UtcNow;
        await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
        await using var connection = await dataSource.OpenConnectionAsync(CancellationToken.None);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO auctions
                (id, tenant_id, title, description, starting_price, minimum_bid_increment, current_bid_amount, current_bidder_id,
                 start_time_utc, end_time_utc, status, sale_mode, buy_now_price, final_winner_id, final_price,
                 version, created_at_utc, updated_at_utc)
            VALUES
                (@id, @tenantId, 'Scheduler Test Auction', 'Integration test auction', 10000, 500, @currentBidAmount, @currentBidderId,
                @startTimeUtc, @endTimeUtc, @status, @saleMode, @buyNowPrice, @finalWinnerId, @finalPrice,
                @version, @createdAtUtc, @updatedAtUtc);
            """,
            connection);

        command.Parameters.AddWithValue("id", auctionId);
        command.Parameters.AddWithValue("tenantId", TenantDefaults.DemoTenantId);
        command.Parameters.AddWithValue("currentBidAmount", currentBidAmount is null ? DBNull.Value : currentBidAmount.Value);
        command.Parameters.AddWithValue("currentBidderId", string.IsNullOrWhiteSpace(currentBidderId) ? DBNull.Value : currentBidderId);
        command.Parameters.AddWithValue("startTimeUtc", now.AddHours(-1));
        command.Parameters.AddWithValue("endTimeUtc", now.AddMinutes(endOffsetMinutes));
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("saleMode", saleMode);
        command.Parameters.AddWithValue("buyNowPrice", buyNowPrice is null ? DBNull.Value : buyNowPrice.Value);
        command.Parameters.AddWithValue("finalWinnerId", finalWinnerId is null ? DBNull.Value : finalWinnerId);
        command.Parameters.AddWithValue("finalPrice", finalPrice is null ? DBNull.Value : finalPrice.Value);
        command.Parameters.AddWithValue("version", version);
        command.Parameters.AddWithValue("createdAtUtc", now);
        command.Parameters.AddWithValue("updatedAtUtc", now);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static async Task InsertOutboxEventAsync(Guid auctionId, string eventType, long aggregateVersion)
    {
        await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
        await using var connection = await dataSource.OpenConnectionAsync(CancellationToken.None);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO outbox_messages
                (id, event_type, aggregate_type, aggregate_id, aggregate_version, occurred_at_utc,
                 correlation_id, payload, created_at_utc, publish_attempts)
            VALUES
                (@id, @eventType, 'Auction', @auctionId, @aggregateVersion, @occurredAtUtc,
                 @correlationId, '{}'::jsonb, @createdAtUtc, 0);
            """,
            connection);
        var now = DateTimeOffset.UtcNow;
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("eventType", eventType);
        command.Parameters.AddWithValue("auctionId", auctionId);
        command.Parameters.AddWithValue("aggregateVersion", aggregateVersion);
        command.Parameters.AddWithValue("occurredAtUtc", now);
        command.Parameters.AddWithValue("correlationId", Guid.NewGuid().ToString());
        command.Parameters.AddWithValue("createdAtUtc", now);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static async Task InsertBidAsync(Guid bidId, Guid auctionId, string bidderId, decimal amount, int createdOffsetSeconds)
    {
        await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
        await using var connection = await dataSource.OpenConnectionAsync(CancellationToken.None);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO bids (id, auction_id, bidder_id, amount, created_at_utc)
            VALUES (@id, @auctionId, @bidderId, @amount, @createdAtUtc);
            """,
            connection);

        command.Parameters.AddWithValue("id", bidId);
        command.Parameters.AddWithValue("auctionId", auctionId);
        command.Parameters.AddWithValue("bidderId", bidderId);
        command.Parameters.AddWithValue("amount", amount);
        command.Parameters.AddWithValue("createdAtUtc", DateTimeOffset.UtcNow.AddSeconds(createdOffsetSeconds));
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static async Task SetCurrentBidAsync(Guid auctionId, string bidderId, decimal amount, long version)
    {
        await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
        await using var connection = await dataSource.OpenConnectionAsync(CancellationToken.None);
        await using var command = new NpgsqlCommand(
            """
            UPDATE auctions
            SET current_bidder_id = @bidderId,
                current_bid_amount = @amount,
                version = @version
            WHERE id = @auctionId;
            """,
            connection);

        command.Parameters.AddWithValue("auctionId", auctionId);
        command.Parameters.AddWithValue("bidderId", bidderId);
        command.Parameters.AddWithValue("amount", amount);
        command.Parameters.AddWithValue("version", version);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static async Task<AuctionRow> GetAuctionAsync(Guid auctionId)
    {
        await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
        await using var connection = await dataSource.OpenConnectionAsync(CancellationToken.None);
        await using var command = new NpgsqlCommand(
            "SELECT status, version, sale_mode, final_winner_id, final_price FROM auctions WHERE id = @auctionId",
            connection);
        command.Parameters.AddWithValue("auctionId", auctionId);
        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        Assert.True(await reader.ReadAsync(CancellationToken.None));
        return new AuctionRow(
            reader.GetString(0),
            reader.GetInt64(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetDecimal(4));
    }

    private static async Task<List<OutboxRow>> GetOutboxMessagesAsync(Guid auctionId)
    {
        await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
        await using var connection = await dataSource.OpenConnectionAsync(CancellationToken.None);
        await using var command = new NpgsqlCommand(
            """
            SELECT event_type, aggregate_version, correlation_id, payload::text
            FROM outbox_messages
            WHERE aggregate_id = @auctionId
            ORDER BY created_at_utc, event_type;
            """,
            connection);
        command.Parameters.AddWithValue("auctionId", auctionId);

        var rows = new List<OutboxRow>();
        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        while (await reader.ReadAsync(CancellationToken.None))
        {
            rows.Add(new OutboxRow(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3)));
        }

        return rows;
    }

    private static async Task<int> GetOutboxCountAsync()
    {
        await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
        await using var connection = await dataSource.OpenConnectionAsync(CancellationToken.None);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM outbox_messages", connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync(CancellationToken.None), CultureInfo.InvariantCulture);
    }

    private sealed record AuctionRow(
        string Status,
        long Version,
        string SaleMode,
        string? FinalWinnerId,
        decimal? FinalPrice);
    private sealed record OutboxRow(string EventType, long AggregateVersion, string? CorrelationId, string Payload);
    private sealed record ColumnExpectation(string TypeCategory, bool Nullable);
    private sealed record ColumnMetadata(string DataType, string UdtName, bool IsNullable);
}
