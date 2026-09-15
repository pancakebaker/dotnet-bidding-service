// <copyright file="TenantDefaults.cs" company="Distributed Bidding Auction Platform">
// Copyright (c) Distributed Bidding Auction Platform. Licensed under the MIT license.
// </copyright>
namespace bidding_service.Domain;

/// <summary>
/// Temporary single-tenant compatibility values used until tenant-aware request context is added.
/// </summary>
public static class TenantDefaults
{
    /// <summary>Stable owner for current local/demo data and single-tenant API requests.</summary>
    public static readonly Guid DemoTenantId =
        Guid.Parse("aaaaaaaa-1111-4111-8111-111111111111");

    /// <summary>Stable display name for the current local/demo tenant.</summary>
    public const string DemoTenantName = "Local Demo Tenant";

    /// <summary>Stable identifier for the local Laravel client registry record.</summary>
    public static readonly Guid DemoClientApplicationId =
        Guid.Parse("aaaaaaaa-7777-4777-8777-777777777777");

    /// <summary>Stable application identifier used by the local Laravel installation.</summary>
    public const string DemoClientId = "local-laravel-client";

    /// <summary>Stable display name for the local Laravel client.</summary>
    public const string DemoClientApplicationName = "Local Laravel Client";
}
