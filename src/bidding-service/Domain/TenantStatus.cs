// <copyright file="TenantStatus.cs" company="Distributed Bidding Auction Platform">
// Copyright (c) Distributed Bidding Auction Platform. Licensed under the MIT license.
// </copyright>
namespace bidding_service.Domain;

/// <summary>Describes a tenant's authoritative runtime access state.</summary>
public enum TenantStatus
{
    /// <summary>The tenant is available for normal operations.</summary>
    Active,
    /// <summary>The tenant may read but cannot perform mutations.</summary>
    Suspended,
    /// <summary>The tenant is retained but cannot use tenant-facing runtime APIs.</summary>
    Disabled
}
