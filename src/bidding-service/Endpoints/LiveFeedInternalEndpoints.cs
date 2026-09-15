// <copyright file="LiveFeedInternalEndpoints.cs" company="Distributed Bidding Auction Platform">
// Copyright (c) Distributed Bidding Auction Platform. Licensed under the MIT license.
// </copyright>
using System.Data.Common;

using bidding_service.Contracts;
using bidding_service.Security.LiveFeed;
using bidding_service.Services;

namespace bidding_service.Endpoints;

/// <summary>Requests the public Live Feed decision for one auction.</summary>
public sealed record LiveFeedAuctionAccessRequest(Guid AuctionId);

/// <summary>Maps the authenticated internal Live Feed decision boundary.</summary>
public static class LiveFeedInternalEndpoints
{
    /// <summary>Registers the internal Live Feed access endpoint.</summary>
    public static void MapLiveFeedInternalEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/internal/live-feed/access", async (
                LiveFeedAuctionAccessRequest request,
                ITenantRuntimeAccessPolicy policy,
                CancellationToken cancellationToken) =>
            {
                TenantRuntimeAccessDecision decision;
                try
                {
                    decision = await policy.EvaluateAuctionLiveFeedAsync(request.AuctionId, cancellationToken);
                }
                catch (DbException)
                {
                    return Results.Json(
                        new ApiErrorResponse(
                            "tenant_access_unavailable",
                            "Tenant access could not be evaluated."),
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }
                if (decision.StatusCode != StatusCodes.Status200OK)
                {
                    return Results.Json(
                        new ApiErrorResponse(decision.ErrorCode, decision.Message),
                        statusCode: decision.StatusCode);
                }

                return Results.Ok(new { allowed = true });
            })
            .AddEndpointFilter<LiveFeedServiceAuthenticationFilter>()
            .WithTags("Internal")
            .ExcludeFromDescription();
    }
}
