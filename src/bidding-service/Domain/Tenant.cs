// <copyright file="Tenant.cs" company="Distributed Bidding Auction Platform">
// Copyright (c) Distributed Bidding Auction Platform. Licensed under the MIT license.
// </copyright>
namespace bidding_service.Domain;

/// <summary>
/// Represents the authoritative customer boundary for bidding resources.
/// </summary>
public sealed class Tenant
{
    /// <summary>Creates a tenant after validating its identity and display name.</summary>
    public static Tenant Create(
        Guid id,
        string name,
        TenantStatus status,
        DateTimeOffset now)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("Tenant ID must not be empty.", nameof(id));

        var displayName = name.Trim();
        if (displayName.Length == 0)
            throw new ArgumentException("Tenant name must not be empty.", nameof(name));

        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status));

        return new Tenant
        {
            Id = id,
            Name = displayName,
            Status = status,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
    }

    /// <summary>Gets or sets the opaque tenant identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the display name.</summary>
    public required string Name { get; set; }

    /// <summary>Gets or sets the persisted lifecycle status.</summary>
    public TenantStatus Status { get; set; }

    /// <summary>Gets or sets the monotonic status-transition version.</summary>
    public long Version { get; set; } = 1;

    /// <summary>Gets or sets the creation timestamp.</summary>
    public DateTimeOffset CreatedAtUtc { get; set; }

    /// <summary>Gets or sets the last update timestamp.</summary>
    public DateTimeOffset UpdatedAtUtc { get; set; }

    /// <summary>Gets the registered applications owned by this tenant.</summary>
    public ICollection<ClientApplication> ClientApplications { get; } = new List<ClientApplication>();
}
