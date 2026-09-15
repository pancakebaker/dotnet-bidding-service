using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using bidding_service.Data;
using bidding_service.Domain;
using bidding_service.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace bidding_service.Tests;

[Collection("Bidding service database")]
public sealed class TenantAdministrationEndpointTests : IClassFixture<AuctionApiFactory>, IAsyncLifetime
{
    private const string Endpoint = "/api/system/tenants/aaaaaaaa-1111-4111-8111-111111111111/status";
    private readonly AuctionApiFactory factory;
    private readonly HttpClient client;

    public TenantAdministrationEndpointTests(AuctionApiFactory factory)
    {
        this.factory = factory;
        client = factory.CreateClient();
    }

    public Task InitializeAsync() => factory.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task SystemAdministratorListsTenantLifecycleState()
    {
        UseSystemAdminToken();

        var response = await client.GetAsync("/api/system/tenants");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<List<TenantStatusResponse>>();
        var tenant = Assert.Single(body!);
        Assert.Equal(TenantDefaults.DemoTenantId, tenant.TenantId);
        Assert.Equal("Local Demo Tenant", tenant.Name);
        Assert.Equal(TenantStatus.Active, tenant.Status);
        Assert.Equal(1, tenant.Version);
    }

    [Fact]
    public async Task SystemAdministratorChangesStatusAndCreatesVersionedOutboxEvent()
    {
        UseSystemAdminToken();

        var response = await client.PatchAsJsonAsync(Endpoint, new
        {
            status = "Suspended",
            expectedVersion = 1
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<TenantStatusResponse>();
        Assert.NotNull(body);
        Assert.Equal(TenantStatus.Suspended, body.Status);
        Assert.Equal(2, body.Version);

        var message = Assert.Single(await ReadOutboxAsync());
        Assert.Equal("TenantStatusChanged", message.EventType);
        Assert.Equal("Tenant", message.AggregateType);
        Assert.Equal(2, message.AggregateVersion);
        using var payload = JsonDocument.Parse(message.Payload);
        Assert.Equal("Active", payload.RootElement.GetProperty("previousStatus").GetString());
        Assert.Equal("Suspended", payload.RootElement.GetProperty("currentStatus").GetString());
        Assert.Equal(2, payload.RootElement.GetProperty("tenantVersion").GetInt64());
        Assert.True(payload.RootElement.GetProperty("tenantId").GetGuid() != Guid.Empty);
        Assert.Equal(message.Id, payload.RootElement.GetProperty("eventId").GetGuid());

        var history = await client.GetFromJsonAsync<List<TenantStatusTransitionResponse>>(
            $"/api/system/tenants/{TenantDefaults.DemoTenantId:D}/status-history");
        var transition = Assert.Single(history!);
        Assert.Equal(TenantStatus.Active, transition.PreviousStatus);
        Assert.Equal(TenantStatus.Suspended, transition.CurrentStatus);
        Assert.Equal(2, transition.TenantVersion);
        Assert.Equal("system-admin-test-subject", transition.ChangedBySubject);
    }

    [Theory]
    [InlineData(TenantStatus.Active, TenantStatus.Suspended)]
    [InlineData(TenantStatus.Active, TenantStatus.Disabled)]
    [InlineData(TenantStatus.Suspended, TenantStatus.Active)]
    [InlineData(TenantStatus.Suspended, TenantStatus.Disabled)]
    [InlineData(TenantStatus.Disabled, TenantStatus.Active)]
    [InlineData(TenantStatus.Disabled, TenantStatus.Suspended)]
    public async Task AllActualStatusTransitionsAreSupported(
        TenantStatus currentStatus,
        TenantStatus requestedStatus)
    {
        await SetTenantStateForTestAsync(currentStatus, 1);
        UseSystemAdminToken();

        var response = await client.PatchAsJsonAsync(Endpoint, new
        {
            status = requestedStatus.ToString(),
            expectedVersion = 1
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<TenantStatusResponse>();
        Assert.NotNull(body);
        Assert.Equal(requestedStatus, body.Status);
        Assert.Equal(2, body.Version);
    }

    [Fact]
    public async Task NoOpReturnsCurrentStateWithoutAdvancingVersionOrCreatingOutboxEvent()
    {
        UseSystemAdminToken();

        var response = await client.PatchAsJsonAsync(Endpoint, new
        {
            status = "Active",
            expectedVersion = 1
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<TenantStatusResponse>();
        Assert.NotNull(body);
        Assert.Equal(1, body.Version);
        Assert.Empty(await ReadOutboxAsync());
        Assert.Empty(await ReadHistoryAsync());
    }

    [Fact]
    public async Task HistoryIsDescendingAndSupportsVersionCursor()
    {
        await TransitionAsync("Suspended", 1);
        await TransitionAsync("Disabled", 2);
        await TransitionAsync("Active", 3);

        var first = await client.GetFromJsonAsync<List<TenantStatusTransitionResponse>>(
            $"/api/system/tenants/{TenantDefaults.DemoTenantId:D}/status-history?limit=2");
        Assert.Equal([4L, 3L], first!.Select(item => item.TenantVersion));

        var second = await client.GetFromJsonAsync<List<TenantStatusTransitionResponse>>(
            $"/api/system/tenants/{TenantDefaults.DemoTenantId:D}/status-history?limit=2&beforeVersion=3");
        Assert.Equal([2L], second!.Select(item => item.TenantVersion));
    }

    [Fact]
    public async Task HistoryRejectsInvalidPagingAndUnknownTenant()
    {
        UseSystemAdminToken();
        var invalid = await client.GetAsync(
            $"/api/system/tenants/{TenantDefaults.DemoTenantId:D}/status-history?limit=101");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        var missing = await client.GetAsync(
            "/api/system/tenants/11111111-2222-4333-8444-555555555555/status-history");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task MissingInvalidAndStaleRequestsDoNotMutateOrCreateOutboxEvents()
    {
        UseSystemAdminToken();
        var invalid = await client.PatchAsJsonAsync(Endpoint, new
        {
            status = "Archived",
            expectedVersion = 1
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        var missing = await client.PatchAsJsonAsync(
            "/api/system/tenants/11111111-2222-4333-8444-555555555555/status",
            new { status = "Disabled", expectedVersion = 1 });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        var stale = await client.PatchAsJsonAsync(Endpoint, new
        {
            status = "Disabled",
            expectedVersion = 2
        });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Empty(await ReadOutboxAsync());
    }

    [Theory]
    [InlineData("999")]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("2")]
    [InlineData("Archived")]
    [InlineData("disabled")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task InvalidStatusNamesAreRejectedBeforeAdministration(string? status)
    {
        var administration = new UnexpectedAdministrationService();
        using var testHost = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddSingleton<ITenantStatusAdministrationService>(administration)));
        using var requestClient = testHost.CreateClient();
        requestClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", JwtTestKeys.CreateSystemAdminToken());
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BiddingDbContext>();
        var before = await db.Tenants.AsNoTracking().SingleAsync(
            item => item.Id == TenantDefaults.DemoTenantId);

        var response = await requestClient.PatchAsJsonAsync(
            Endpoint, new { status, expectedVersion = before.Version });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, administration.Calls);
        var after = await db.Tenants.AsNoTracking().SingleAsync(
            item => item.Id == TenantDefaults.DemoTenantId);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(before.UpdatedAtUtc, after.UpdatedAtUtc);
        Assert.Empty(await ReadOutboxAsync());
    }

    [Fact]
    public async Task NonSystemAdminIdentitiesAreRejected()
    {
        var anonymous = await client.PatchAsJsonAsync(Endpoint, new { status = "Disabled", expectedVersion = 1 });
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        var anonymousHistory = await client.GetAsync(
            $"/api/system/tenants/{TenantDefaults.DemoTenantId:D}/status-history");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousHistory.StatusCode);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            JwtTestKeys.CreateToken(permissions: ["auction.manage"]));
        var tenantUser = await client.PatchAsJsonAsync(Endpoint, new { status = "Disabled", expectedVersion = 1 });
        Assert.Equal(HttpStatusCode.Unauthorized, tenantUser.StatusCode);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            JwtTestKeys.CreateServiceToken(issuedAt: TestAuctionData.Now));
        var liveFeed = await client.PatchAsJsonAsync(Endpoint, new { status = "Disabled", expectedVersion = 1 });
        Assert.Equal(HttpStatusCode.Unauthorized, liveFeed.StatusCode);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            JwtTestKeys.CreateSystemAdminToken(permissions: []));
        var unprivilegedSystemToken = await client.PatchAsJsonAsync(
            Endpoint,
            new { status = "Disabled", expectedVersion = 1 });
        Assert.Equal(HttpStatusCode.Forbidden, unprivilegedSystemToken.StatusCode);
        var forbiddenHistory = await client.GetAsync(
            $"/api/system/tenants/{TenantDefaults.DemoTenantId:D}/status-history");
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenHistory.StatusCode);
    }

    [Fact]
    public async Task ConcurrentAdminRequestsCommitOneTransitionAndOneOutboxEvent()
    {
        using var firstClient = factory.CreateClient();
        using var secondClient = factory.CreateClient();
        firstClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            JwtTestKeys.CreateSystemAdminToken());
        secondClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            JwtTestKeys.CreateSystemAdminToken());

        var suspended = firstClient.PatchAsJsonAsync(Endpoint, new
        {
            status = "Suspended",
            expectedVersion = 1
        });
        var disabled = secondClient.PatchAsJsonAsync(Endpoint, new
        {
            status = "Disabled",
            expectedVersion = 1
        });

        var responses = await Task.WhenAll(suspended, disabled);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        var message = Assert.Single(await ReadOutboxAsync());
        Assert.Equal(2, message.AggregateVersion);
        Assert.Single(await ReadHistoryAsync());
    }

    [Fact]
    public async Task SequentialTransitionsProduceStrictlyIncreasingTenantVersions()
    {
        await TransitionAsync("Disabled", 1);
        await TransitionAsync("Active", 2);
        await TransitionAsync("Suspended", 3);

        var messages = await ReadOutboxAsync();
        Assert.Equal([2L, 3L, 4L], messages.Select(message => message.AggregateVersion));
    }

    [Fact]
    public async Task OutboxPersistenceFailureDoesNotCommitTenantStatus()
    {
        string connectionString;
        using (var scope = factory.Services.CreateScope())
        {
            var sourceDb = scope.ServiceProvider.GetRequiredService<BiddingDbContext>();
            connectionString = sourceDb.Database.GetDbConnection().ConnectionString;
        }

        var options = new DbContextOptionsBuilder<BiddingDbContext>()
            .UseNpgsql(connectionString)
            .AddInterceptors(new ThrowOnOutboxSaveInterceptor())
            .Options;
        await using var db = new BiddingDbContext(options);
        var service = new TenantStatusAdministrationService(db, new FixedTimeProvider(TestAuctionData.Now));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ChangeStatusAsync(
            TenantDefaults.DemoTenantId,
            TenantStatus.Disabled,
            1,
            "system-admin-test-subject",
            "atomicity-test"));

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<BiddingDbContext>();
        var tenant = await verifyDb.Tenants.AsNoTracking().SingleAsync(
            item => item.Id == TenantDefaults.DemoTenantId);
        Assert.Equal(TenantStatus.Active, tenant.Status);
        Assert.Equal(1, tenant.Version);
        Assert.Empty(await ReadOutboxAsync());
        Assert.Empty(await ReadHistoryAsync());
    }

    private void UseSystemAdminToken() =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            JwtTestKeys.CreateSystemAdminToken());

    private async Task TransitionAsync(string status, long expectedVersion)
    {
        UseSystemAdminToken();
        var response = await client.PatchAsJsonAsync(Endpoint, new { status, expectedVersion });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task SetTenantStateForTestAsync(TenantStatus status, long version)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BiddingDbContext>();
        var tenant = await db.Tenants.SingleAsync(item => item.Id == TenantDefaults.DemoTenantId);
        tenant.Status = status;
        tenant.Version = version;
        await db.SaveChangesAsync();
    }

    private async Task<List<OutboxMessage>> ReadOutboxAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BiddingDbContext>();
        return await db.OutboxMessages.AsNoTracking().ToListAsync();
    }

    private async Task<List<TenantStatusTransition>> ReadHistoryAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BiddingDbContext>();
        return await db.TenantStatusTransitions.AsNoTracking().ToListAsync();
    }

    private sealed record TenantStatusResponse(
        Guid TenantId,
        string Name,
        TenantStatus Status,
        long Version,
        DateTimeOffset UpdatedAtUtc);

    private sealed record TenantStatusTransitionResponse(
        Guid TransitionId,
        Guid TenantId,
        TenantStatus PreviousStatus,
        TenantStatus CurrentStatus,
        long TenantVersion,
        DateTimeOffset ChangedAtUtc,
        string ChangedBySubject,
        string CorrelationId);

    private sealed class UnexpectedAdministrationService : ITenantStatusAdministrationService
    {
        public int Calls { get; private set; }

        public Task<TenantStatusTransitionResult?> ChangeStatusAsync(
            Guid tenantId,
            TenantStatus requestedStatus,
            long expectedVersion,
            string changedBySubject,
            string correlationId,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("Invalid status reached administration.");
        }
    }

    private sealed class ThrowOnOutboxSaveInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context?.ChangeTracker.Entries<OutboxMessage>()
                .Any(entry => entry.State == EntityState.Added) == true)
            {
                throw new InvalidOperationException("Injected outbox persistence failure.");
            }

            return ValueTask.FromResult(result);
        }
    }
}
