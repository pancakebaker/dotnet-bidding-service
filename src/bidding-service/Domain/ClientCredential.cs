// <copyright file="ClientCredential.cs" company="Distributed Bidding Auction Platform">
// Copyright (c) Distributed Bidding Auction Platform. Licensed under the MIT license.
// </copyright>
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace bidding_service.Domain;

/// <summary>
/// Stores public verification material for a registered client application.
/// Private key material is never accepted or persisted by this entity.
/// </summary>
public sealed class ClientCredential
{
    private static readonly Regex KeyIdPattern =
        new("^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$", RegexOptions.CultureInvariant);

    private ClientCredential()
    {
    }

    /// <summary>Creates an active RSA public-key credential.</summary>
    public static ClientCredential Create(
        Guid id,
        Guid clientApplicationId,
        string keyId,
        string publicKeyPem,
        DateTimeOffset validFromUtc,
        DateTimeOffset? expiresAtUtc,
        DateTimeOffset now)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("Credential ID must not be empty.", nameof(id));

        if (clientApplicationId == Guid.Empty)
            throw new ArgumentException("Client application ID must not be empty.", nameof(clientApplicationId));

        EnsureUtc(validFromUtc, nameof(validFromUtc));
        EnsureUtc(now, nameof(now));
        if (expiresAtUtc is not null)
            EnsureUtc(expiresAtUtc.Value, nameof(expiresAtUtc));

        var normalizedKeyId = NormalizeKeyId(keyId);
        var canonicalPublicKeyPem = NormalizePublicKey(publicKeyPem, out var fingerprint);
        if (expiresAtUtc is not null && expiresAtUtc <= validFromUtc)
            throw new ArgumentException("Credential expiry must be after its validity start.", nameof(expiresAtUtc));

        return new ClientCredential
        {
            Id = id,
            ClientApplicationId = clientApplicationId,
            KeyId = normalizedKeyId,
            PublicKeyPem = canonicalPublicKeyPem,
            PublicKeyFingerprint = fingerprint,
            Status = ClientCredentialStatus.Active,
            ValidFromUtc = validFromUtc,
            ExpiresAtUtc = expiresAtUtc,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
    }

    /// <summary>Normalizes and validates a non-secret key selector.</summary>
    public static string NormalizeKeyId(string keyId)
    {
        var normalized = keyId.Trim().ToLowerInvariant();
        if (!KeyIdPattern.IsMatch(normalized))
        {
            throw new ArgumentException(
                "Key ID must contain 1-63 lowercase letters, numbers, or hyphens and may not start or end with a hyphen.",
                nameof(keyId));
        }

        return normalized;
    }

    /// <summary>Returns whether this credential is usable at the supplied UTC instant.</summary>
    public bool IsUsableAt(DateTimeOffset now)
    {
        EnsureUtc(now, nameof(now));
        return Status == ClientCredentialStatus.Active
            && RevokedAtUtc is null
            && now >= ValidFromUtc
            && (ExpiresAtUtc is null || now < ExpiresAtUtc);
    }

    /// <summary>Revokes this credential without deleting its audit history.</summary>
    public void Revoke(DateTimeOffset now)
    {
        EnsureUtc(now, nameof(now));
        if (Status == ClientCredentialStatus.Revoked)
            return;

        Status = ClientCredentialStatus.Revoked;
        RevokedAtUtc = now;
        UpdatedAtUtc = now;
    }

    /// <summary>Gets the immutable credential identifier.</summary>
    public Guid Id { get; private set; }

    /// <summary>Gets the immutable owning application identifier.</summary>
    public Guid ClientApplicationId { get; private set; }

    /// <summary>Gets the stable, non-secret JWT key selector.</summary>
    public string KeyId { get; private set; } = string.Empty;

    /// <summary>Gets the canonical SubjectPublicKeyInfo PEM.</summary>
    public string PublicKeyPem { get; private set; } = string.Empty;

    /// <summary>Gets the lowercase SHA-256 fingerprint of the public key DER.</summary>
    public string PublicKeyFingerprint { get; private set; } = string.Empty;

    /// <summary>Gets the lifecycle status.</summary>
    public ClientCredentialStatus Status { get; private set; }

    /// <summary>Gets the UTC validity start.</summary>
    public DateTimeOffset ValidFromUtc { get; private set; }

    /// <summary>Gets the optional UTC validity end.</summary>
    public DateTimeOffset? ExpiresAtUtc { get; private set; }

    /// <summary>Gets the UTC revocation timestamp.</summary>
    public DateTimeOffset? RevokedAtUtc { get; private set; }

    /// <summary>Gets the UTC creation timestamp.</summary>
    public DateTimeOffset CreatedAtUtc { get; private set; }

    /// <summary>Gets the UTC update timestamp.</summary>
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    private static string NormalizePublicKey(string publicKeyPem, out string fingerprint)
    {
        if (string.IsNullOrWhiteSpace(publicKeyPem))
            throw new ArgumentException("A public key is required.", nameof(publicKeyPem));

        if (publicKeyPem.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Private key material must not be registered.", nameof(publicKeyPem));

        using var rsa = RSA.Create();
        try
        {
            rsa.ImportFromPem(publicKeyPem);
        }
        catch (Exception exception) when (exception is ArgumentException or CryptographicException)
        {
            throw new ArgumentException("The public key must be a valid RSA PEM key.", nameof(publicKeyPem), exception);
        }

        if (rsa.KeySize < 2048)
            throw new ArgumentException("RSA public keys must be at least 2048 bits.", nameof(publicKeyPem));

        var der = rsa.ExportSubjectPublicKeyInfo();
        fingerprint = Convert.ToHexString(SHA256.HashData(der)).ToLowerInvariant();
        return rsa.ExportSubjectPublicKeyInfoPem();
    }

    private static void EnsureUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
            throw new ArgumentException("Credential timestamps must use UTC.", parameterName);
    }
}
