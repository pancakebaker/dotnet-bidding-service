// <copyright file="TenantAdministrationEndpoints.cs" company="Distributed Bidding Auction Platform">
// Copyright (c) Distributed Bidding Auction Platform. Licensed under the MIT license.
// </copyright>
using System.Security.Claims;
using bidding_service.Contracts;
using bidding_service.Data;
using bidding_service.Domain;
using bidding_service.Services;
using Microsoft.EntityFrameworkCore;

namespace bidding_service.Endpoints;

/// <summary>Request for an optimistic-concurrency-protected tenant status change.</summary>
public sealed record ChangeTenantStatusRequest(string? Status, long ExpectedVersion);

/// <summary>Durable tenant lifecycle transition returned to SystemAdministrators.</summary>
public sealed record TenantStatusTransitionResponse(
    Guid TransitionId,
    Guid TenantId,
    TenantStatus PreviousStatus,
    TenantStatus CurrentStatus,
    long TenantVersion,
    DateTimeOffset ChangedAtUtc,
    string ChangedBySubject,
    string CorrelationId);

/// <summary>Maps system-administrator tenant lifecycle endpoints.</summary>
public static class TenantAdministrationEndpoints
{
    /// <summary>Registers the system-administrator tenant status endpoint.</summary>
    public static void MapTenantAdministrationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/system/tenants", async (
                BiddingDbContext db,
                CancellationToken cancellationToken) =>
            {
                var tenants = await db.Tenants
                    .AsNoTracking()
                    .OrderBy(tenant => tenant.Name)
                    .Select(tenant => new TenantStatusResponse(
                        tenant.Id,
                        tenant.Name,
                        tenant.Status,
                        tenant.Version,
                        tenant.UpdatedAtUtc))
                    .ToListAsync(cancellationToken);

                return Results.Ok(tenants);
            })
            .RequireAuthorization("SystemAdminTenantStatus")
            .WithTags("System administration")
            .ExcludeFromDescription();

        app.MapGet("/api/system/tenants/{tenantId:guid}/status-history", async (
                Guid tenantId,
                int? limit,
                long? beforeVersion,
                BiddingDbContext db,
                CancellationToken cancellationToken) =>
            {
                var pageSize = limit ?? 25;
                if (pageSize is < 1 or > 100 || beforeVersion is <= 0)
                {
                    return Results.BadRequest(new ApiErrorResponse(
                        "invalid_tenant_history_paging",
                        "limit must be between 1 and 100 and beforeVersion must be positive."));
                }

                if (!await db.Tenants.AsNoTracking().AnyAsync(item => item.Id == tenantId, cancellationToken))
                {
                    return Results.NotFound(new ApiErrorResponse("tenant_not_found", "Tenant not found."));
                }

                var query = db.TenantStatusTransitions
                    .AsNoTracking()
                    .Where(item => item.TenantId == tenantId);
                if (beforeVersion.HasValue)
                    query = query.Where(item => item.TenantVersion < beforeVersion.Value);

                var history = await query
                    .OrderByDescending(item => item.TenantVersion)
                    .Take(pageSize)
                    .Select(item => new TenantStatusTransitionResponse(
                        item.Id,
                        item.TenantId,
                        item.PreviousStatus,
                        item.CurrentStatus,
                        item.TenantVersion,
                        item.ChangedAtUtc,
                        item.ChangedBySubject,
                        item.CorrelationId))
                    .ToListAsync(cancellationToken);
                return Results.Ok(history);
            })
            .RequireAuthorization("SystemAdminTenantStatus")
            .WithTags("System administration")
            .ExcludeFromDescription();

        app.MapPatch("/api/system/tenants/{tenantId:guid}/status", async (
                Guid tenantId,
                ChangeTenantStatusRequest request,
                ITenantStatusAdministrationService service,
                HttpContext context,
                CancellationToken cancellationToken) =>
            {
                if (request.Status is null
                    || !Enum.GetNames<TenantStatus>().Contains(request.Status, StringComparer.Ordinal)
                    || !Enum.TryParse<TenantStatus>(request.Status, ignoreCase: false, out var status)
                    || request.ExpectedVersion < 1)
                {
                    return Results.BadRequest(new ApiErrorResponse(
                        "invalid_tenant_status_request",
                        "Status and a positive expectedVersion are required."));
                }

                var correlationId = context.Request.Headers["X-Correlation-ID"].FirstOrDefault()?.Trim();
                if (string.IsNullOrWhiteSpace(correlationId)
                    || correlationId.Length > 128
                    || correlationId.Any(char.IsControl))
                    correlationId = Guid.NewGuid().ToString("N");

                var changedBySubject = context.User.FindFirstValue(ClaimTypes.NameIdentifier)
                    ?? context.User.FindFirstValue("sub");
                if (string.IsNullOrWhiteSpace(changedBySubject))
                    return Results.Forbid();

                TenantStatusTransitionResult? result;
                try
                {
                    result = await service.ChangeStatusAsync(
                        tenantId,
                        status,
                        request.ExpectedVersion,
                        changedBySubject,
                        correlationId,
                        cancellationToken);
                }
                catch (TenantStatusConcurrencyException)
                {
                    return Results.Conflict(new ApiErrorResponse(
                        "tenant_status_conflict",
                        "Tenant status changed concurrently. Refresh and retry."));
                }

                if (result is null)
                {
                    return Results.NotFound(new ApiErrorResponse("tenant_not_found", "Tenant not found."));
                }

                return Results.Ok(new TenantStatusResponse(
                    result.Tenant.Id,
                    result.Tenant.Name,
                    result.Tenant.Status,
                    result.Tenant.Version,
                    result.Tenant.UpdatedAtUtc));
            })
            .RequireAuthorization("SystemAdminTenantStatus")
            .WithTags("System administration")
            .ExcludeFromDescription();
    }
}

/// <summary>Current authoritative tenant lifecycle state.</summary>
public sealed record TenantStatusResponse(
    Guid TenantId,
    string Name,
    TenantStatus Status,
    long Version,
    DateTimeOffset UpdatedAtUtc);
