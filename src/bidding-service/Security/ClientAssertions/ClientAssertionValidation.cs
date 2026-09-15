// <copyright file="ClientAssertionValidation.cs" company="Distributed Bidding Auction Platform">
// Copyright (c) Distributed Bidding Auction Platform. Licensed under the MIT license.
// </copyright>
using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using bidding_service.Data;
using bidding_service.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace bidding_service.Security.ClientAssertions;

/// <summary>Internal reasons for rejecting a client assertion.</summary>
public enum ClientAssertionFailureReason
{
    /// <summary>The token could not be parsed.</summary>
    MalformedToken,
    /// <summary>The token exceeds the parser input limit.</summary>
    OversizedToken,
    /// <summary>The token does not use RS256.</summary>
    UnsupportedAlgorithm,
    /// <summary>The token has no key identifier.</summary>
    MissingKeyId,
    /// <summary>The key identifier is not canonical.</summary>
    InvalidKeyId,
    /// <summary>The issuer application is not registered.</summary>
    UnknownApplication,
    /// <summary>The issuer application is disabled.</summary>
    ApplicationDisabled,
    /// <summary>The issuer application is revoked.</summary>
    ApplicationRevoked,
    /// <summary>The selected credential is not registered.</summary>
    UnknownCredential,
    /// <summary>The selected credential belongs to another application.</summary>
    CredentialMismatch,
    /// <summary>The selected credential is revoked.</summary>
    CredentialRevoked,
    /// <summary>The selected credential is not yet valid.</summary>
    CredentialNotYetValid,
    /// <summary>The selected credential has expired.</summary>
    CredentialExpired,
    /// <summary>The signature is invalid.</summary>
    InvalidSignature,
    /// <summary>The issuer is invalid.</summary>
    InvalidIssuer,
    /// <summary>The audience is invalid.</summary>
    InvalidAudience,
    /// <summary>The tenant claim is missing.</summary>
    MissingTenant,
    /// <summary>The tenant claim is not a valid identifier.</summary>
    InvalidTenant,
    /// <summary>The tenant claim does not match the application.</summary>
    TenantMismatch,
    /// <summary>The JTI claim is missing.</summary>
    MissingJti,
    /// <summary>The JTI claim is not valid.</summary>
    InvalidJti,
    /// <summary>The issued-at claim is missing.</summary>
    MissingIssuedAt,
    /// <summary>The not-before claim is missing.</summary>
    MissingNotBefore,
    /// <summary>The expiry claim is missing.</summary>
    MissingExpiry,
    /// <summary>The assertion timestamp window is invalid.</summary>
    InvalidTimeWindow,
    /// <summary>The assertion is not yet valid.</summary>
    AssertionNotYetValid,
    /// <summary>The assertion is expired.</summary>
    AssertionExpired,
    /// <summary>The token claims failed validation.</summary>
    InvalidTokenClaims,
    /// <summary>The stored credential key could not be parsed.</summary>
    CredentialKeyInvalid
}

/// <summary>Strongly typed identity proven by a valid client assertion.</summary>
public sealed record ValidatedClientIdentity(
    Guid ClientApplicationId,
    string ClientId,
    Guid TenantId,
    Guid ClientCredentialId,
    string KeyId,
    string Jti,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string PublicKeyFingerprint);

/// <summary>Result of validating an application client assertion.</summary>
public sealed record ClientAssertionValidationResult(
    ValidatedClientIdentity? Identity,
    ClientAssertionFailureReason? FailureReason)
{
    /// <summary>Gets whether validation succeeded.</summary>
    public bool IsValid => Identity is not null && FailureReason is null;

    /// <summary>Creates a successful result.</summary>
    public static ClientAssertionValidationResult Success(ValidatedClientIdentity identity) =>
        new(identity, null);

    /// <summary>Creates a controlled failure result.</summary>
    public static ClientAssertionValidationResult Failure(ClientAssertionFailureReason reason) =>
        new(null, reason);
}

/// <summary>Validates signed application assertions without enforcing them on HTTP requests.</summary>
public interface IClientAssertionValidator
{
    /// <summary>Validates one RS256 client assertion.</summary>
    Task<ClientAssertionValidationResult> ValidateAsync(
        string assertion,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Validates the cryptographic proof and registry relationship for a client application.
/// Replay protection and HTTP admission are deliberately deferred to later MT5.3 phases.
/// </summary>
public sealed class ClientAssertionValidator(
    BiddingDbContext db,
    TimeProvider timeProvider) : IClientAssertionValidator
{
    private const string Audience = "dbap-bidding-service";
    private static readonly TimeSpan MaximumLifetime = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(30);
    private static readonly Regex JtiPattern =
        new("^[A-Za-z0-9._~-]{1,128}$", RegexOptions.CultureInvariant);

    /// <inheritdoc />
    public async Task<ClientAssertionValidationResult> ValidateAsync(
        string assertion,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(assertion))
            return ClientAssertionValidationResult.Failure(ClientAssertionFailureReason.MalformedToken);

        if (assertion.Length > 16_384)
            return ClientAssertionValidationResult.Failure(ClientAssertionFailureReason.OversizedToken);

        JwtSecurityToken token;
        try
        {
            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
            if (!handler.CanReadToken(assertion) || assertion.Split('.').Length != 3)
                return ClientAssertionValidationResult.Failure(ClientAssertionFailureReason.MalformedToken);

            token = handler.ReadJwtToken(assertion);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or SecurityTokenException)
        {
            return ClientAssertionValidationResult.Failure(ClientAssertionFailureReason.MalformedToken);
        }

        if (!string.Equals(token.Header.Alg, SecurityAlgorithms.RsaSha256, StringComparison.Ordinal))
            return ClientAssertionValidationResult.Failure(ClientAssertionFailureReason.UnsupportedAlgorithm);

        var rawKeyId = token.Header.Kid;
        if (string.IsNullOrWhiteSpace(rawKeyId))
            return ClientAssertionValidationResult.Failure(ClientAssertionFailureReason.MissingKeyId);

        string keyId;
        try
        {
            keyId = ClientCredential.NormalizeKeyId(rawKeyId);
        }
        catch (ArgumentException)
        {
            return ClientAssertionValidationResult.Failure(ClientAssertionFailureReason.InvalidKeyId);
        }

        if (!string.Equals(rawKeyId, keyId, StringComparison.Ordinal))
            return ClientAssertionValidationResult.Failure(ClientAssertionFailureReason.InvalidKeyId);

        var rawIssuer = token.Issuer;
        if (string.IsNullOrWhiteSpace(rawIssuer))
            return ClientAssertionValidationResult.Failure(ClientAssertionFailureReason.InvalidIssuer);

        string clientId;
        try
        {
            clientId = ClientApplication.NormalizeClientId(rawIssuer);
        }
        catch (ArgumentException)
        {
            return ClientAssertionValidationResult.Failure(ClientAssertionFailureReason.InvalidIssuer);
        }

        if (!string.Equals(rawIssuer, clientId, StringComparison.Ordinal))
            return ClientAssertionValidationResult.Failure(ClientAssertionFailureReason.InvalidIssuer);

        var application = await db.ClientApplications
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.ClientId == clientId, cancellationToken);
        if (application is null)
            return ClientAssertionValidationResult.Failure(ClientAssertionFailureReason.UnknownApplication);

        if (application.Status == ClientApplicationStatus.Disabled)
            return ClientAssertionValidationResult.Failure(ClientAssertionFailureReason.ApplicationDisabled);

        if (application.Status == ClientApplicationStatus.Revoked)
            return ClientAssertionValidationResult.Failure(ClientAssertionFailureReason.ApplicationRevoked);

        var credential = await db.ClientCredentials
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.KeyId == keyId, cancellationToken);
        if (credential is null)
            return ClientAssertionValidationResult.Failure(ClientAssertionFailureReason.UnknownCredential);

        if (credential.ClientApplicationId != application.Id)
            return ClientAssertionValidationResult.Failure(ClientAssertionFailureReason.CredentialMismatch);

        var now = timeProvider.GetUtcNow();
        if (credential.Status == ClientCredentialStatus.Revoked)
            return ClientAssertionValidationResult.Failure(ClientAssertionFailureReason.CredentialRevoked);

        if (now < credential.ValidFromUtc)
            return ClientAssertionValidationResult.Failure(ClientAssertionFailureReason.CredentialNotYetValid);

        if (credential.ExpiresAtUtc is not null && now >= credential.ExpiresAtUtc)
            return ClientAssertionValidationResult.Failure(ClientAssertionFailureReason.CredentialExpired);

        SecurityKey publicKey;
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(credential.PublicKeyPem);
            publicKey = new RsaSecurityKey(rsa.ExportParameters(false));
        }
        catch (Exception exception) when (exception is ArgumentException or CryptographicException)
        {
            return ClientAssertionValidationResult.Failure(ClientAssertionFailureReason.CredentialKeyInvalid);
        }

        var issuedAt = ParseRequiredTimestamp(token, JwtRegisteredClaimNames.Iat);
        var notBefore = ParseRequiredTimestamp(token, JwtRegisteredClaimNames.Nbf);
        var expiresAt = ParseRequiredTimestamp(token, JwtRegisteredClaimNames.Exp);
        if (issuedAt is null)
            return ClientAssertionValidationResult.Failure(ClientAssertionFailureReason.MissingIssuedAt);
        if (notBefore is null)
            return ClientAssertionValidationResult.Failure(ClientAssertionFailureReason.MissingNotBefore);
        if (expiresAt is null)
            return ClientAssertionValidationResult.Failure(ClientAssertionFailureReason.MissingExpiry);

        if (expiresAt <= issuedAt || expiresAt - issuedAt > MaximumLifetime)
            return ClientAssertionValidationResult.Failure(ClientAssertionFailureReason.InvalidTimeWindow);
        if (issuedAt > now + ClockSkew || notBefore > now + ClockSkew)
            return ClientAssertionValidationResult.Failure(ClientAssertionFailureReason.AssertionNotYetValid);
        if (now >= expiresAt + ClockSkew)
            return ClientAssertionValidationResult.Failure(ClientAssertionFailureReason.AssertionExpired);
        if (notBefore > issuedAt + ClockSkew)
            return ClientAssertionValidationResult.Failure(ClientAssertionFailureReason.InvalidTimeWindow);

        var jti = token.Claims.SingleOrDefault(claim => claim.Type == JwtRegisteredClaimNames.Jti)?.Value;
        if (string.IsNullOrWhiteSpace(jti))
            return ClientAssertionValidationResult.Failure(ClientAssertionFailureReason.MissingJti);
        if (!JtiPattern.IsMatch(jti))
            return ClientAssertionValidationResult.Failure(ClientAssertionFailureReason.InvalidJti);

        var tenantClaim = token.Claims.SingleOrDefault(claim => claim.Type == "tenant_id")?.Value;
        if (string.IsNullOrWhiteSpace(tenantClaim))
            return ClientAssertionValidationResult.Failure(ClientAssertionFailureReason.MissingTenant);
        if (!Guid.TryParse(tenantClaim, out var tenantId) || tenantId == Guid.Empty)
            return ClientAssertionValidationResult.Failure(ClientAssertionFailureReason.InvalidTenant);
        if (tenantId != application.TenantId)
            return ClientAssertionValidationResult.Failure(ClientAssertionFailureReason.TenantMismatch);

        var validationParameters = new TokenValidationParameters
        {
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = publicKey,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            ValidateIssuer = true,
            ValidIssuer = application.ClientId,
            ValidateAudience = true,
            ValidAudience = Audience,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            ClockSkew = ClockSkew,
            LifetimeValidator = (_, _, _, _) => true
        };

        try
        {
            new JwtSecurityTokenHandler { MapInboundClaims = false }
                .ValidateToken(assertion, validationParameters, out _);
        }
        catch (SecurityTokenInvalidIssuerException)
        {
            return ClientAssertionValidationResult.Failure(ClientAssertionFailureReason.InvalidIssuer);
        }
        catch (SecurityTokenInvalidAudienceException)
        {
            return ClientAssertionValidationResult.Failure(ClientAssertionFailureReason.InvalidAudience);
        }
        catch (SecurityTokenExpiredException)
        {
            return ClientAssertionValidationResult.Failure(ClientAssertionFailureReason.AssertionExpired);
        }
        catch (SecurityTokenNotYetValidException)
        {
            return ClientAssertionValidationResult.Failure(ClientAssertionFailureReason.AssertionNotYetValid);
        }
        catch (SecurityTokenInvalidSignatureException)
        {
            return ClientAssertionValidationResult.Failure(ClientAssertionFailureReason.InvalidSignature);
        }
        catch (SecurityTokenException)
        {
            return ClientAssertionValidationResult.Failure(ClientAssertionFailureReason.InvalidTokenClaims);
        }

        return ClientAssertionValidationResult.Success(new ValidatedClientIdentity(
            application.Id,
            application.ClientId,
            application.TenantId,
            credential.Id,
            credential.KeyId,
            jti,
            issuedAt.Value,
            expiresAt.Value,
            credential.PublicKeyFingerprint));
    }

    private static DateTimeOffset? ParseRequiredTimestamp(JwtSecurityToken token, string claimType)
    {
        var value = token.Claims.SingleOrDefault(claim => claim.Type == claimType)?.Value;
        return long.TryParse(value, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
    }
}
