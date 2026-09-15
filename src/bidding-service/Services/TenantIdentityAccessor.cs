// <copyright file="TenantIdentityAccessor.cs" company="Distributed Bidding Auction Platform">
// Copyright (c) Distributed Bidding Auction Platform. Licensed under the MIT license.
// </copyright>
using System.Security.Claims;

namespace bidding_service.Services;

/// <summary>
/// Resolves the trusted tenant identity carried by an authenticated service token.
/// </summary>
public interface ITenantIdentityAccessor
{
    /// <summary>Tries to resolve the canonical tenant claim.</summary>
    bool TryGetTenantId(ClaimsPrincipal principal, out Guid tenantId);
}

/// <summary>Reads only the validated <c>tenant_id</c> claim.</summary>
public sealed class TenantIdentityAccessor : ITenantIdentityAccessor
{
    /// <inheritdoc />
    public bool TryGetTenantId(ClaimsPrincipal principal, out Guid tenantId)
    {
        tenantId = Guid.Empty;
        var value = principal.FindFirstValue("tenant_id");
        return value is not null && Guid.TryParse(value, out tenantId) && tenantId != Guid.Empty;
    }
}
