// <copyright file="DatabaseSeeder.cs" company="Distributed Bidding Auction Platform">
// Copyright (c) Distributed Bidding Auction Platform. Licensed under the MIT license.
// </copyright>
using bidding_service.Domain;
using Microsoft.EntityFrameworkCore;

namespace bidding_service.Data;

/// <summary>
/// Creates deterministic local demo auction data.
/// </summary>
public static class DatabaseSeeder
{
    /// <summary>
    /// Stable identifier for the open MacBook Pro demo auction.
    /// </summary>
    public static readonly Guid OpenAuctionId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    /// <summary>
    /// Stable identifier for the scheduled Camera demo auction.
    /// </summary>
    public static readonly Guid ScheduledAuctionId =
        Guid.Parse("22222222-2222-2222-2222-222222222222");
    /// <summary>
    /// Stable identifier for the closed Gaming Console demo auction.
    /// </summary>
    public static readonly Guid ClosedAuctionId =
        Guid.Parse("33333333-3333-3333-3333-333333333333");
    /// <summary>
    /// Stable identifier for the open Buy Now-only demo auction.
    /// </summary>
    public static readonly Guid OpenBuyNowOnlyAuctionId =
        Guid.Parse("66666666-6666-6666-6666-666666666666");
    /// <summary>
    /// Stable identifier for the open combined-mode demo auction.
    /// </summary>
    public static readonly Guid OpenAuctionAndBuyNowAuctionId =
        Guid.Parse("77777777-7777-7777-7777-777777777777");
    /// <summary>
    /// Stable identifier for the scheduled combined-mode demo auction.
    /// </summary>
    public static readonly Guid ScheduledAuctionAndBuyNowAuctionId =
        Guid.Parse("88888888-8888-8888-8888-888888888888");

    /// <summary>
    /// Seeds deterministic demo auctions and bid history when the database is empty.
    /// </summary>
    public static async Task SeedAsync(
        BiddingDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var tenant = await db.Tenants.SingleOrDefaultAsync(
            item => item.Id == TenantDefaults.DemoTenantId,
            cancellationToken);
        if (tenant is null)
        {
            db.Tenants.Add(Tenant.Create(
                TenantDefaults.DemoTenantId,
                TenantDefaults.DemoTenantName,
                TenantStatus.Active,
                now));
            await db.SaveChangesAsync(cancellationToken);
        }

        var clientApplication = await db.ClientApplications.SingleOrDefaultAsync(
            item => item.Id == TenantDefaults.DemoClientApplicationId,
            cancellationToken);
        if (clientApplication is null)
        {
            db.ClientApplications.Add(ClientApplication.Create(
                TenantDefaults.DemoClientApplicationId,
                TenantDefaults.DemoClientId,
                TenantDefaults.DemoTenantId,
                TenantDefaults.DemoClientApplicationName,
                ClientApplicationStatus.Active,
                now));
            await db.SaveChangesAsync(cancellationToken);
        }

        if (await db.Auctions.AnyAsync(cancellationToken))
        {
            return;
        }

        var createdAt = now.AddDays(-2);

        db.Auctions.AddRange(
            new Auction
            {
                Id = OpenAuctionId,
                TenantId = TenantDefaults.DemoTenantId,
                Title = "MacBook Pro",
                Description = "Demo open auction for a laptop.",
                StartingPrice = 1000m,
                SaleMode = SaleMode.AuctionOnly,
                MinimumBidIncrement = 50m,
                CurrentBidAmount = 1150m,
                CurrentBidderId = "carol",
                StartTimeUtc = now.AddHours(-2),
                EndTimeUtc = now.AddHours(6),
                Status = AuctionStatus.Open,
                Version = 3,
                CreatedAtUtc = createdAt,
                UpdatedAtUtc = now.AddMinutes(-15)
            },
            new Auction
            {
                Id = ScheduledAuctionId,
                TenantId = TenantDefaults.DemoTenantId,
                Title = "Camera",
                Description = "Demo scheduled auction for a camera kit.",
                StartingPrice = 500m,
                SaleMode = SaleMode.AuctionOnly,
                MinimumBidIncrement = 25m,
                CurrentBidAmount = null,
                CurrentBidderId = null,
                StartTimeUtc = now.AddHours(3),
                EndTimeUtc = now.AddHours(10),
                Status = AuctionStatus.Scheduled,
                Version = 1,
                CreatedAtUtc = createdAt,
                UpdatedAtUtc = createdAt
            },
            new Auction
            {
                Id = ClosedAuctionId,
                TenantId = TenantDefaults.DemoTenantId,
                Title = "Gaming Console",
                Description = "Demo closed auction for a gaming console.",
                StartingPrice = 300m,
                SaleMode = SaleMode.AuctionOnly,
                MinimumBidIncrement = 20m,
                CurrentBidAmount = 380m,
                CurrentBidderId = "erin",
                StartTimeUtc = now.AddDays(-2),
                EndTimeUtc = now.AddHours(-1),
                Status = AuctionStatus.Closed,
                Version = 2,
                CreatedAtUtc = now.AddDays(-3),
                UpdatedAtUtc = now.AddHours(-1)
            },
            new Auction
            {
                Id = OpenBuyNowOnlyAuctionId,
                TenantId = TenantDefaults.DemoTenantId,
                Title = "Noise-Cancelling Headphones",
                Description = "Demo Buy Now-only auction.",
                StartingPrice = 500m,
                SaleMode = SaleMode.BuyNowOnly,
                BuyNowPrice = 500m,
                MinimumBidIncrement = 1m,
                StartTimeUtc = now.AddHours(-1),
                EndTimeUtc = now.AddHours(8),
                Status = AuctionStatus.Open,
                Version = 1,
                CreatedAtUtc = createdAt,
                UpdatedAtUtc = createdAt
            },
            new Auction
            {
                Id = OpenAuctionAndBuyNowAuctionId,
                TenantId = TenantDefaults.DemoTenantId,
                Title = "Mirrorless Camera Body",
                Description = "Demo auction with ordinary bidding and Buy Now.",
                StartingPrice = 100m,
                SaleMode = SaleMode.AuctionAndBuyNow,
                BuyNowPrice = 500m,
                MinimumBidIncrement = 25m,
                StartTimeUtc = now.AddHours(-1),
                EndTimeUtc = now.AddHours(8),
                Status = AuctionStatus.Open,
                Version = 1,
                CreatedAtUtc = createdAt,
                UpdatedAtUtc = createdAt
            },
            new Auction
            {
                Id = ScheduledAuctionAndBuyNowAuctionId,
                TenantId = TenantDefaults.DemoTenantId,
                Title = "Studio Microphone",
                Description = "Demo scheduled auction with Buy Now.",
                StartingPrice = 100m,
                SaleMode = SaleMode.AuctionAndBuyNow,
                BuyNowPrice = 500m,
                MinimumBidIncrement = 25m,
                StartTimeUtc = now.AddHours(3),
                EndTimeUtc = now.AddHours(12),
                Status = AuctionStatus.Scheduled,
                Version = 1,
                CreatedAtUtc = createdAt,
                UpdatedAtUtc = createdAt
            });

        db.Bids.AddRange(
            new Bid
            {
                Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa1"),
                AuctionId = OpenAuctionId,
                BidderId = "alice",
                Amount = 1000m,
                CreatedAtUtc = now.AddMinutes(-75)
            },
            new Bid
            {
                Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa2"),
                AuctionId = OpenAuctionId,
                BidderId = "bob",
                Amount = 1100m,
                CreatedAtUtc = now.AddMinutes(-45)
            },
            new Bid
            {
                Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa3"),
                AuctionId = OpenAuctionId,
                BidderId = "carol",
                Amount = 1150m,
                CreatedAtUtc = now.AddMinutes(-15)
            },
            new Bid
            {
                Id = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbb1"),
                AuctionId = ClosedAuctionId,
                BidderId = "dave",
                Amount = 320m,
                CreatedAtUtc = now.AddDays(-1).AddHours(-2)
            },
            new Bid
            {
                Id = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbb2"),
                AuctionId = ClosedAuctionId,
                BidderId = "erin",
                Amount = 380m,
                CreatedAtUtc = now.AddDays(-1).AddHours(-1)
            });

        await db.SaveChangesAsync(cancellationToken);
    }
}
