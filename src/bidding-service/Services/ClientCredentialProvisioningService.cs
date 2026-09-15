// <copyright file="ClientCredentialProvisioningService.cs" company="Distributed Bidding Auction Platform">
// Copyright (c) Distributed Bidding Auction Platform. Licensed under the MIT license.
// </copyright>
using bidding_service.Data;
using bidding_service.Domain;
using Microsoft.EntityFrameworkCore;

namespace bidding_service.Services;

/// <summary>
/// Provides an internal provisioning boundary for public client credentials.
/// It is not exposed as an HTTP endpoint and does not participate in request admission.
/// </summary>
public interface IClientCredentialProvisioningService
{
    /// <summary>Registers a public key for the application identified by client ID.</summary>
    Task<ClientCredential> ProvisionAsync(
        string clientId,
        string keyId,
        string publicKeyPem,
        DateTimeOffset validFromUtc,
        DateTimeOffset? expiresAtUtc,
        CancellationToken cancellationToken = default);

    /// <summary>Revokes a credential by its stable key ID.</summary>
    Task<bool> RevokeAsync(string keyId, CancellationToken cancellationToken = default);
}

/// <summary>Persists public-key credentials and their lifecycle transitions.</summary>
public sealed class ClientCredentialProvisioningService(
    BiddingDbContext db,
    TimeProvider timeProvider) : IClientCredentialProvisioningService
{
    /// <inheritdoc />
    public async Task<ClientCredential> ProvisionAsync(
        string clientId,
        string keyId,
        string publicKeyPem,
        DateTimeOffset validFromUtc,
        DateTimeOffset? expiresAtUtc,
        CancellationToken cancellationToken = default)
    {
        var normalizedClientId = ClientApplication.NormalizeClientId(clientId);
        var application = await db.ClientApplications
            .SingleOrDefaultAsync(item => item.ClientId == normalizedClientId, cancellationToken);

        if (application is null)
            throw new InvalidOperationException($"Client application '{normalizedClientId}' was not found.");

        if (application.Status == ClientApplicationStatus.Revoked)
            throw new InvalidOperationException("Credentials cannot be added to a revoked client application.");

        var normalizedKeyId = ClientCredential.NormalizeKeyId(keyId);
        if (await db.ClientCredentials.AnyAsync(item => item.KeyId == normalizedKeyId, cancellationToken))
            throw new InvalidOperationException($"Credential key ID '{normalizedKeyId}' is already registered.");

        var credential = ClientCredential.Create(
            Guid.NewGuid(),
            application.Id,
            normalizedKeyId,
            publicKeyPem,
            validFromUtc,
            expiresAtUtc,
            timeProvider.GetUtcNow());

        if (await db.ClientCredentials.AnyAsync(
                item => item.PublicKeyFingerprint == credential.PublicKeyFingerprint,
                cancellationToken))
        {
            throw new InvalidOperationException("The public key is already registered.");
        }

        db.ClientCredentials.Add(credential);
        await db.SaveChangesAsync(cancellationToken);
        return credential;
    }

    /// <inheritdoc />
    public async Task<bool> RevokeAsync(string keyId, CancellationToken cancellationToken = default)
    {
        var normalizedKeyId = ClientCredential.NormalizeKeyId(keyId);
        var credential = await db.ClientCredentials
            .SingleOrDefaultAsync(item => item.KeyId == normalizedKeyId, cancellationToken);

        if (credential is null)
            return false;

        credential.Revoke(timeProvider.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
