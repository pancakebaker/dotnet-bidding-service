// <copyright file="TenantStatusAdministrationService.cs" company="Distributed Bidding Auction Platform">
// Copyright (c) Distributed Bidding Auction Platform. Licensed under the MIT license.
// </copyright>
using bidding_service.Data;
using bidding_service.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace bidding_service.Services;

/// <summary>Result of an authoritative tenant status transition.</summary>
public sealed record TenantStatusTransitionResult(
    Tenant Tenant,
    TenantStatus PreviousStatus,
    bool Changed);

/// <summary>Changes tenant status and publishes its committed transition through the outbox.</summary>
public interface ITenantStatusAdministrationService
{
    /// <summary>Changes a tenant status using an explicit expected version.</summary>
    Task<TenantStatusTransitionResult?> ChangeStatusAsync(
        Guid tenantId,
        TenantStatus requestedStatus,
        long expectedVersion,
        string changedBySubject,
        string correlationId,
        CancellationToken cancellationToken = default);
}

/// <summary>Authoritative application boundary for system-admin tenant status changes.</summary>
public sealed class TenantStatusAdministrationService(
    BiddingDbContext db,
    TimeProvider timeProvider) : ITenantStatusAdministrationService
{
    /// <inheritdoc />
    public async Task<TenantStatusTransitionResult?> ChangeStatusAsync(
        Guid tenantId,
        TenantStatus requestedStatus,
        long expectedVersion,
        string changedBySubject,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        var tenant = await db.Tenants.SingleOrDefaultAsync(
            item => item.Id == tenantId,
            cancellationToken);
        if (tenant is null)
            return null;

        if (tenant.Version != expectedVersion)
            throw new TenantStatusConcurrencyException();

        var previousStatus = tenant.Status;
        if (previousStatus == requestedStatus)
            return new TenantStatusTransitionResult(tenant, previousStatus, false);

        tenant.Status = requestedStatus;
        tenant.Version++;
        var changedAtUtc = timeProvider.GetUtcNow();
        tenant.UpdatedAtUtc = changedAtUtc;
        db.TenantStatusTransitions.Add(new TenantStatusTransition
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            PreviousStatus = previousStatus,
            CurrentStatus = requestedStatus,
            TenantVersion = tenant.Version,
            ChangedAtUtc = changedAtUtc,
            ChangedBySubject = changedBySubject,
            CorrelationId = correlationId
        });
        db.OutboxMessages.Add(OutboxMessageFactory.TenantStatusChanged(
            tenant,
            previousStatus,
            correlationId,
            changedAtUtc));

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new TenantStatusConcurrencyException(exception);
        }
        catch (DbUpdateException exception) when (IsLifecycleVersionConflict(exception))
        {
            throw new TenantStatusConcurrencyException(exception);
        }

        return new TenantStatusTransitionResult(tenant, previousStatus, true);
    }

    private static bool IsLifecycleVersionConflict(DbUpdateException exception)
    {
        return exception.InnerException is PostgresException postgresException
            && postgresException.SqlState == PostgresErrorCodes.UniqueViolation
            && postgresException.ConstraintName == "ux_tenant_status_transitions_tenant_version";
    }
}

/// <summary>Indicates that a tenant changed after an admin read its expected version.</summary>
public sealed class TenantStatusConcurrencyException(Exception? innerException = null)
    : Exception("The tenant status changed concurrently.", innerException);
