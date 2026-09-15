// <copyright file="ClientApplication.cs" company="Distributed Bidding Auction Platform">
// Copyright (c) Distributed Bidding Auction Platform. Licensed under the MIT license.
// </copyright>
using System.Text.RegularExpressions;

namespace bidding_service.Domain;

/// <summary>
/// Represents a registered application belonging to one tenant.
/// Credentials and admission enforcement are intentionally deferred.
/// </summary>
public sealed class ClientApplication
{
    private static readonly Regex ClientIdPattern =
        new("^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$", RegexOptions.CultureInvariant);

    /// <summary>Creates a client application after validating its registry identity.</summary>
    public static ClientApplication Create(
        Guid id,
        string clientId,
        Guid tenantId,
        string name,
        ClientApplicationStatus status,
        DateTimeOffset now)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("Client application ID must not be empty.", nameof(id));

        if (tenantId == Guid.Empty)
            throw new ArgumentException("Tenant ID must not be empty.", nameof(tenantId));

        var normalizedClientId = NormalizeClientId(clientId);
        var displayName = name.Trim();
        if (displayName.Length == 0)
            throw new ArgumentException("Client application name must not be empty.", nameof(name));

        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status));

        return new ClientApplication
        {
            Id = id,
            ClientId = normalizedClientId,
            TenantId = tenantId,
            Name = displayName,
            Status = status,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
    }

    /// <summary>Normalizes and validates a stable, non-secret client identifier.</summary>
    public static string NormalizeClientId(string clientId)
    {
        var normalized = clientId.Trim().ToLowerInvariant();
        if (!ClientIdPattern.IsMatch(normalized))
        {
            throw new ArgumentException(
                "Client ID must contain 1-63 lowercase letters, numbers, or hyphens and may not start or end with a hyphen.",
                nameof(clientId));
        }

        return normalized;
    }

    /// <summary>Gets the immutable registry identifier.</summary>
    public Guid Id { get; init; }

    /// <summary>Gets the immutable, globally unique application identifier.</summary>
    public string ClientId { get; init; } = string.Empty;

    /// <summary>Gets the immutable owning tenant identifier.</summary>
    public Guid TenantId { get; init; }

    /// <summary>Gets or sets the display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the administrative status.</summary>
    public ClientApplicationStatus Status { get; set; }

    /// <summary>Gets or sets the creation timestamp.</summary>
    public DateTimeOffset CreatedAtUtc { get; init; }

    /// <summary>Gets or sets the last update timestamp.</summary>
    public DateTimeOffset UpdatedAtUtc { get; set; }

    /// <summary>Gets the credentials registered for this application.</summary>
    public ICollection<ClientCredential> ClientCredentials { get; } = new List<ClientCredential>();
}
