using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using bidding_service.Data;
using bidding_service.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace bidding_service.Tests;

[Collection("Bidding service database")]
public sealed class LiveFeedInternalEndpointTests : IClassFixture<AuctionApiFactory>, IAsyncLifetime
{
    private readonly AuctionApiFactory factory;
    private readonly HttpClient client;

    public LiveFeedInternalEndpointTests(AuctionApiFactory factory)
    {
        this.factory = factory;
        client = factory.CreateClient();
    }

    public Task InitializeAsync() => factory.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ServiceTokenAllowsActiveAndSuspendedButDeniesDisabledAuction()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BiddingDbContext>();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", JwtTestKeys.CreateServiceToken(issuedAt: TestAuctionData.Now));
        var active = await client.PostAsJsonAsync(
            "/internal/live-feed/access",
            new { auctionId = TestAuctionData.OpenAuctionId });
        Assert.Equal(HttpStatusCode.OK, active.StatusCode);

        await db.Tenants.Where(item => item.Id == TenantDefaults.DemoTenantId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, TenantStatus.Suspended));
        var suspended = await client.PostAsJsonAsync(
            "/internal/live-feed/access",
            new { auctionId = TestAuctionData.OpenAuctionId });
        Assert.Equal(HttpStatusCode.OK, suspended.StatusCode);

        await db.Tenants.Where(item => item.Id == TenantDefaults.DemoTenantId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, TenantStatus.Disabled));
        var disabled = await client.PostAsJsonAsync(
            "/internal/live-feed/access",
            new { auctionId = TestAuctionData.OpenAuctionId });
        Assert.Equal(HttpStatusCode.Forbidden, disabled.StatusCode);
    }

    [Fact]
    public async Task InternalEndpointRejectsMissingOrInvalidServiceToken()
    {
        var missing = await client.PostAsJsonAsync(
            "/internal/live-feed/access",
            new { auctionId = TestAuctionData.OpenAuctionId });
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-jwt");
        var invalid = await client.PostAsJsonAsync(
            "/internal/live-feed/access",
            new { auctionId = TestAuctionData.OpenAuctionId });
        Assert.Equal(HttpStatusCode.Unauthorized, invalid.StatusCode);
    }

    [Fact]
    public async Task InternalEndpointReturnsNotFoundForUnknownAuction()
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", JwtTestKeys.CreateServiceToken(issuedAt: TestAuctionData.Now));
        var response = await client.PostAsJsonAsync(
            "/internal/live-feed/access",
            new { auctionId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
