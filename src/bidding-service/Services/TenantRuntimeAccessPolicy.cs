// <copyright file="TenantRuntimeAccessPolicy.cs" company="Distributed Bidding Auction Platform">
// Copyright (c) Distributed Bidding Auction Platform. Licensed under the MIT license.
// </copyright>
using System.Data.Common;
using bidding_service.Contracts;
using bidding_service.Data;
using bidding_service.Domain;
using Microsoft.EntityFrameworkCore;

namespace bidding_service.Services;

/// <summary>Identifies the tenant-facing operation being authorized.</summary>
public enum TenantRuntimeOperation
{
    /// <summary>Tenant-facing read operation.</summary>
    Read,
    /// <summary>Tenant-facing mutation.</summary>
    Mutate,
    /// <summary>Tenant-facing live-feed subscription.</summary>
    SubscribeLiveFeed
}

/// <summary>Endpoint metadata declaring the tenant runtime operation.</summary>
public sealed record TenantRuntimeAccessMetadata(TenantRuntimeOperation Operation);

/// <summary>Authoritative tenant state resolved for the current request.</summary>
public sealed record TenantRuntimeContext(Guid TenantId, TenantStatus Status);

/// <summary>Stores the authoritative tenant state for the current request.</summary>
public static class TenantRuntimeRequestContext
{
    private static readonly object Key = new();

    /// <summary>Stores the resolved tenant state on the request.</summary>
    public static void Set(HttpContext httpContext, TenantRuntimeContext context) =>
        httpContext.Items[Key] = context;

    /// <summary>Tries to read the resolved tenant state from the request.</summary>
    public static bool TryGet(HttpContext httpContext, out TenantRuntimeContext? context)
    {
        context = httpContext.Items[Key] as TenantRuntimeContext;
        return context is not null;
    }
}

/// <summary>Result of evaluating a tenant's runtime status.</summary>
public sealed record TenantRuntimeAccessDecision(
    TenantRuntimeContext? Context,
    int StatusCode,
    string ErrorCode,
    string Message)
{
    /// <summary>Gets whether the operation may continue.</summary>
    public bool IsAllowed => Context is not null && StatusCode == StatusCodes.Status200OK;

    /// <summary>Creates an allowed decision.</summary>
    public static TenantRuntimeAccessDecision Allow(TenantRuntimeContext context) =>
        new(context, StatusCodes.Status200OK, string.Empty, string.Empty);

    /// <summary>Creates a controlled denial.</summary>
    public static TenantRuntimeAccessDecision Deny(
        TenantRuntimeContext? context,
        string errorCode,
        string message) =>
        new(context, StatusCodes.Status403Forbidden, errorCode, message);

    /// <summary>Creates a not-found decision.</summary>
    public static TenantRuntimeAccessDecision NotFound(string errorCode, string message) =>
        new(null, StatusCodes.Status404NotFound, errorCode, message);
}

/// <summary>Reads authoritative tenant state and applies runtime status semantics.</summary>
public interface ITenantRuntimeAccessPolicy
{
    /// <summary>Evaluates a tenant-facing operation.</summary>
    Task<TenantRuntimeAccessDecision> EvaluateAsync(
        Guid tenantId,
        TenantRuntimeOperation operation,
        Guid? resourceId,
        CancellationToken cancellationToken = default);

    /// <summary>Evaluates whether an auction may expose public Live Feed.</summary>
    Task<TenantRuntimeAccessDecision> EvaluateAuctionLiveFeedAsync(
        Guid auctionId,
        CancellationToken cancellationToken = default);
}

/// <summary>Central tenant status policy for tenant-facing runtime operations.</summary>
public sealed class TenantRuntimeAccessPolicy(BiddingDbContext db) : ITenantRuntimeAccessPolicy
{
    /// <inheritdoc />
    public async Task<TenantRuntimeAccessDecision> EvaluateAuctionLiveFeedAsync(
        Guid auctionId,
        CancellationToken cancellationToken = default)
    {
        var auction = await db.Auctions.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == auctionId,
            cancellationToken);
        if (auction is null)
            return TenantRuntimeAccessDecision.NotFound("auction_not_found", "Auction not found.");

        return await EvaluateAsync(
            auction.TenantId,
            TenantRuntimeOperation.SubscribeLiveFeed,
            auction.Id,
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<TenantRuntimeAccessDecision> EvaluateAsync(
        Guid tenantId,
        TenantRuntimeOperation operation,
        Guid? resourceId,
        CancellationToken cancellationToken = default)
    {
        var tenant = await db.Tenants
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == tenantId, cancellationToken);

        if (tenant is null)
        {
            return TenantRuntimeAccessDecision.Deny(
                null,
                "tenant_access_denied",
                "Tenant access is not available.");
        }

        var context = new TenantRuntimeContext(tenant.Id, tenant.Status);
        if (tenant.Status == TenantStatus.Active
            || (tenant.Status == TenantStatus.Suspended && operation != TenantRuntimeOperation.Mutate))
        {
            return TenantRuntimeAccessDecision.Allow(context);
        }

        // Preserve the existing resource-hiding contract. The endpoint handler
        // performs its tenant-scoped lookup and returns 404 for foreign/missing
        // resources; only an owned resource is denied here as 403.
        if (resourceId.HasValue
            && !await db.Auctions.AsNoTracking().AnyAsync(
                auction => auction.Id == resourceId.Value && auction.TenantId == tenantId,
                cancellationToken))
        {
            return TenantRuntimeAccessDecision.Allow(context);
        }

        return tenant.Status switch
        {
            TenantStatus.Suspended => TenantRuntimeAccessDecision.Deny(
                context,
                "tenant_suspended",
                "Tenant is suspended and cannot perform mutations."),
            TenantStatus.Disabled => TenantRuntimeAccessDecision.Deny(
                context,
                "tenant_disabled",
                "Tenant is disabled."),
            _ => TenantRuntimeAccessDecision.Deny(
                context,
                "tenant_access_denied",
                "Tenant access is not available.")
        };
    }
}

/// <summary>Enforces authoritative tenant status before tenant-facing handlers run.</summary>
public sealed class TenantRuntimeStatusFilter(
    ITenantIdentityAccessor tenantIdentityAccessor,
    ITenantRuntimeAccessPolicy accessPolicy) : IEndpointFilter
{
    /// <inheritdoc />
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var metadata = context.HttpContext.GetEndpoint()?.Metadata
            .GetMetadata<TenantRuntimeAccessMetadata>();
        var operation = metadata?.Operation
            ?? (HttpMethods.IsGet(context.HttpContext.Request.Method)
                || HttpMethods.IsHead(context.HttpContext.Request.Method)
                ? TenantRuntimeOperation.Read
                : TenantRuntimeOperation.Mutate);

        if (!tenantIdentityAccessor.TryGetTenantId(context.HttpContext.User, out var tenantId))
        {
            return Results.Json(
                new ApiErrorResponse("tenant_access_denied", "Tenant access is not available."),
                statusCode: StatusCodes.Status403Forbidden);
        }

        Guid? resourceId = null;
        if (context.HttpContext.Request.RouteValues.TryGetValue("id", out var routeValue)
            && Guid.TryParse(routeValue?.ToString(), out var parsedResourceId))
        {
            resourceId = parsedResourceId;
        }

        TenantRuntimeAccessDecision decision;
        try
        {
            decision = await accessPolicy.EvaluateAsync(
                tenantId,
                operation,
                resourceId,
                context.HttpContext.RequestAborted);
        }
        catch (DbException)
        {
            return Results.Json(
                new ApiErrorResponse(
                    "tenant_access_unavailable",
                    "Tenant access could not be evaluated."),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (!decision.IsAllowed)
        {
            return Results.Json(
                new ApiErrorResponse(decision.ErrorCode, decision.Message),
                statusCode: decision.StatusCode);
        }

        if (decision.Context is not null)
        {
            TenantRuntimeRequestContext.Set(context.HttpContext, decision.Context);
        }

        return await next(context);
    }
}
