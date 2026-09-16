using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

using bidding_service.Contracts;
using bidding_service.Data;
using bidding_service.Domain;
using DistributedBidding.IntegrationContracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace bidding_service.Tests;

[Collection("Bidding service database")]
public sealed class ReportingEndpointTests : IClassFixture<AuctionApiFactory>, IAsyncLifetime
{
    private static readonly Guid OtherTenantId =
        Guid.Parse("bbbbbbbb-3333-4333-8333-333333333333");
    private readonly AuctionApiFactory _factory;
    private readonly HttpClient _client;

    public ReportingEndpointTests(AuctionApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            JwtTestKeys.CreateToken(
                permissions: ["auction.read"],
                tenantId: TenantDefaults.DemoTenantId.ToString()));
    }

    public async Task InitializeAsync()
    {
        await _factory.ResetDatabaseAsync();
        await SeedReportingDataAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task DefaultRequestReturnsSevenChronologicalBucketsIncludingZeros()
    {
        var report = await GetReportAsync();

        Assert.Equal(7, report.Days);
        Assert.Equal(7, report.Bids.Count);
        Assert.Equal(7, report.Purchases.Count);
        Assert.Equal(
            report.Bids.Select(item => item.Date).OrderBy(date => date),
            report.Bids.Select(item => item.Date));
        Assert.Contains(report.Bids, item => item.Count == 0);
        Assert.Contains(report.Purchases, item => item.Count == 0);
    }

    [Fact]
    public async Task CountsAcceptedBidsAndPurchasesForCurrentTenantOnly()
    {
        var report = await GetReportAsync(3);

        Assert.Equal(3, report.Days);
        Assert.Equal(2, report.Bids.Single(item => item.Date == "2026-09-04").Count);
        Assert.Equal(1, report.Purchases.Single(item => item.Date == "2026-09-05").Count);
        Assert.Equal(1, report.Purchases.Single(item => item.Date == "2026-09-04").Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    public async Task UnsupportedDaysReturnsValidationError(int days)
    {
        var response = await _client.GetAsync($"/api/reporting/activity?days={days}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ResponseContainsOnlyReportingCounts()
    {
        var response = await _client.GetAsync("/api/reporting/activity");
        var json = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("bidderId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("email", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("auctionId", json, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<ActivityReportResponse> GetReportAsync(int? days = null)
    {
        var response = await _client.GetAsync(
            days.HasValue ? $"/api/reporting/activity?days={days}" : "/api/reporting/activity");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ActivityReportResponse>())!;
    }

    private async Task SeedReportingDataAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BiddingDbContext>();
        db.Bids.RemoveRange(db.Bids);
        db.OutboxMessages.RemoveRange(db.OutboxMessages);
        var otherAuctionId = Guid.Parse("66666666-6666-4666-8666-666666666666");
        db.Tenants.Add(Tenant.Create(
            OtherTenantId,
            "Other tenant",
            TenantStatus.Active,
            TestAuctionData.Now));
        db.Auctions.Add(new Auction
        {
            Id = otherAuctionId,
            TenantId = OtherTenantId,
            Title = "Other tenant auction",
            Description = "Reporting isolation fixture.",
            StartingPrice = 100m,
            SaleMode = SaleMode.AuctionAndBuyNow,
            BuyNowPrice = 500m,
            MinimumBidIncrement = 10m,
            StartTimeUtc = TestAuctionData.Now.AddDays(-10),
            EndTimeUtc = TestAuctionData.Now.AddDays(1),
            Status = AuctionStatus.Open,
            Version = 1,
            CreatedAtUtc = TestAuctionData.Now.AddDays(-10),
            UpdatedAtUtc = TestAuctionData.Now.AddDays(-10)
        });
        await db.SaveChangesAsync();

        db.Bids.AddRange(
            NewBid(TestAuctionData.OpenAuctionId, "bidder-a", TestAuctionData.Now.AddDays(-1)),
            NewBid(TestAuctionData.OpenAuctionId, "bidder-b", TestAuctionData.Now.AddDays(-1)),
            NewBid(otherAuctionId, "other-tenant", TestAuctionData.Now));
        db.OutboxMessages.AddRange(
            NewPurchase(TestAuctionData.OpenAuctionId, TestAuctionData.Now.AddDays(-1), published: false),
            NewPurchase(TestAuctionData.OpenAuctionId, TestAuctionData.Now, published: true),
            NewPurchase(otherAuctionId, TestAuctionData.Now, published: false));
        await db.SaveChangesAsync();
    }

    private static Bid NewBid(Guid auctionId, string bidderId, DateTimeOffset createdAtUtc) => new()
    {
        Id = Guid.NewGuid(),
        AuctionId = auctionId,
        BidderId = bidderId,
        Amount = 100m,
        CreatedAtUtc = createdAtUtc
    };

    private static OutboxMessage NewPurchase(
        Guid auctionId,
        DateTimeOffset occurredAtUtc,
        bool published) => new()
    {
        Id = Guid.NewGuid(),
        EventType = IntegrationEventTypes.AuctionPurchased,
        AggregateType = AggregateTypes.Auction,
        AggregateId = auctionId,
        AggregateVersion = 2,
        OccurredAtUtc = occurredAtUtc,
        Payload = "{}",
        CreatedAtUtc = occurredAtUtc,
        PublishedAtUtc = published ? occurredAtUtc : null,
        PublishAttempts = published ? 1 : 0
    };
}
