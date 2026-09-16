// <copyright file="ReportingEndpoints.cs" company="Distributed Bidding Auction Platform">
// Copyright (c) Distributed Bidding Auction Platform. Licensed under the MIT license.
// </copyright>
using System.Globalization;

using bidding_service.Contracts;
using bidding_service.Data;
using bidding_service.Security.ClientAssertions;
using bidding_service.Services;
using DistributedBidding.IntegrationContracts;
using Microsoft.EntityFrameworkCore;

namespace bidding_service.Endpoints;

/// <summary>Maps authenticated, tenant-scoped reporting endpoints.</summary>
public static class ReportingEndpoints
{
    private const int DefaultDays = 7;
    private const int MaximumDays = 30;

    /// <summary>Registers the activity reporting endpoint.</summary>
    public static void MapReportingEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/reporting/activity", GetActivity)
            .WithName("GetActivityReport")
            .WithTags("Reporting")
            .WithMetadata(new TenantRuntimeAccessMetadata(TenantRuntimeOperation.Read))
            .AddEndpointFilter<ClientAssertionAdmissionFilter>()
            .AddEndpointFilter<TenantRuntimeStatusFilter>()
            .RequireAuthorization("AuctionRead")
            .Produces<ActivityReportResponse>()
            .ProducesValidationProblem();
    }

    private static async Task<IResult> GetActivity(
        int? days,
        BiddingDbContext db,
        TimeProvider timeProvider,
        ITenantIdentityAccessor tenantIdentityAccessor,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var requestedDays = days ?? DefaultDays;
        if (requestedDays is < 1 or > MaximumDays)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["days"] = [$"Days must be between 1 and {MaximumDays}."]
            });
        }

        if (!tenantIdentityAccessor.TryGetTenantId(httpContext.User, out var tenantId))
        {
            return Results.Forbid();
        }

        var today = timeProvider.GetUtcNow().UtcDateTime.Date;
        var from = today.AddDays(-(requestedDays - 1));
        var toExclusive = today.AddDays(1);
        var fromUtc = new DateTimeOffset(from, TimeSpan.Zero);
        var toExclusiveUtc = new DateTimeOffset(toExclusive, TimeSpan.Zero);

        var bidCounts = await db.Bids
            .AsNoTracking()
            .Where(b => b.Auction!.TenantId == tenantId
                && b.CreatedAtUtc >= fromUtc
                && b.CreatedAtUtc < toExclusiveUtc)
            .GroupBy(b => b.CreatedAtUtc.Date)
            .Select(group => new { Date = group.Key, Count = group.LongCount() })
            .ToDictionaryAsync(item => item.Date.Date, item => item.Count, cancellationToken);

        var purchaseCounts = await db.OutboxMessages
            .AsNoTracking()
            .Where(message => message.AggregateType == AggregateTypes.Auction
                && message.EventType == IntegrationEventTypes.AuctionPurchased
                && message.OccurredAtUtc >= fromUtc
                && message.OccurredAtUtc < toExclusiveUtc
                && db.Auctions.Any(auction => auction.Id == message.AggregateId
                    && auction.TenantId == tenantId))
            .GroupBy(message => message.OccurredAtUtc.Date)
            .Select(group => new { Date = group.Key, Count = group.LongCount() })
            .ToDictionaryAsync(item => item.Date.Date, item => item.Count, cancellationToken);

        var dates = Enumerable.Range(0, requestedDays)
            .Select(offset => from.AddDays(offset))
            .ToArray();

        static DailyActivityCount Bucket(DateTime date, IReadOnlyDictionary<DateTime, long> counts) =>
            new(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                counts.TryGetValue(date, out var count) ? count : 0);

        return Results.Ok(new ActivityReportResponse(
            from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            requestedDays,
            dates.Select(date => Bucket(date, bidCounts)).ToArray(),
            dates.Select(date => Bucket(date, purchaseCounts)).ToArray()));
    }
}
