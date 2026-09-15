// <copyright file="LiveFeedServiceTokenValidator.cs" company="Distributed Bidding Auction Platform">
// Copyright (c) Distributed Bidding Auction Platform. Licensed under the MIT license.
// </copyright>
using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using bidding_service.Options;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace bidding_service.Security.LiveFeed;

/// <summary>Identity proven by a valid Live Feed service token.</summary>
public sealed record AuthenticatedServiceContext(
    string Subject,
    string Issuer,
    string Audience,
    string Jti,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string? KeyId);

/// <summary>Result of validating a service token.</summary>
public sealed record ServiceTokenValidationResult(AuthenticatedServiceContext? Context, string? Error)
{
    /// <summary>Gets whether validation succeeded.</summary>
    public bool IsValid => Context is not null;
    /// <summary>Creates a successful validation result.</summary>
    public static ServiceTokenValidationResult Success(AuthenticatedServiceContext context) => new(context, null);
    /// <summary>Creates a failed validation result.</summary>
    public static ServiceTokenValidationResult Failure(string error) => new(null, error);
}

/// <summary>Validates the dedicated Live Feed service identity.</summary>
public interface ILiveFeedServiceTokenValidator
{
    /// <summary>Validates one signed service token.</summary>
    ServiceTokenValidationResult Validate(string token);
}

/// <summary>Validates short-lived RS256 Live Feed service tokens.</summary>
public sealed class LiveFeedServiceTokenValidator(
    IOptions<LiveFeedServiceAuthenticationOptions> options,
    TimeProvider timeProvider) : ILiveFeedServiceTokenValidator
{
    private const int MaximumTokenLength = 16_384;
    private static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    public ServiceTokenValidationResult Validate(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > MaximumTokenLength)
            return ServiceTokenValidationResult.Failure("malformed_service_token");

        var configured = options.Value;
        JwtSecurityToken parsed;
        try
        {
            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
            if (!handler.CanReadToken(token) || token.Split('.').Length != 3)
                return ServiceTokenValidationResult.Failure("malformed_service_token");
            parsed = handler.ReadJwtToken(token);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or SecurityTokenException)
        {
            return ServiceTokenValidationResult.Failure("malformed_service_token");
        }

        if (!string.Equals(parsed.Header.Alg, SecurityAlgorithms.RsaSha256, StringComparison.Ordinal)
            || !string.Equals(parsed.Header.Kid, configured.KeyId, StringComparison.Ordinal))
            return ServiceTokenValidationResult.Failure("invalid_service_token");

        var issuedAt = new DateTimeOffset(DateTime.SpecifyKind(parsed.IssuedAt, DateTimeKind.Utc));
        var notBefore = new DateTimeOffset(DateTime.SpecifyKind(parsed.ValidFrom, DateTimeKind.Utc));
        var expiresAt = new DateTimeOffset(DateTime.SpecifyKind(parsed.ValidTo, DateTimeKind.Utc));

        var now = timeProvider.GetUtcNow();
        var maximumLifetime = TimeSpan.FromSeconds(Math.Clamp(configured.MaximumLifetimeSeconds, 1, 60));
        if (expiresAt <= issuedAt || expiresAt - issuedAt > maximumLifetime
            || issuedAt > now + ClockSkew || notBefore > now + ClockSkew
            || now >= expiresAt + ClockSkew || string.IsNullOrWhiteSpace(parsed.Id))
            return ServiceTokenValidationResult.Failure("invalid_service_token");

        SecurityKey key;
        try
        {
            using var rsa = RSA.Create();
            var publicKeyPath = Path.IsPathRooted(configured.PublicKeyPath)
                ? configured.PublicKeyPath
                : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configured.PublicKeyPath));
            rsa.ImportFromPem(File.ReadAllText(publicKeyPath));
            if (rsa.KeySize < 2048)
                return ServiceTokenValidationResult.Failure("invalid_service_token");
            key = new RsaSecurityKey(rsa.ExportParameters(false));
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or CryptographicException)
        {
            return ServiceTokenValidationResult.Failure("invalid_service_token");
        }

        try
        {
            var principal = new JwtSecurityTokenHandler { MapInboundClaims = false }.ValidateToken(
                token,
                new TokenValidationParameters
                {
                    RequireSignedTokens = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = key,
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                    ValidateIssuer = true,
                    ValidIssuer = configured.Issuer,
                    ValidateAudience = true,
                    ValidAudience = configured.Audience,
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    ClockSkew = ClockSkew,
                    LifetimeValidator = (_, _, _, _) => true
                },
                out _);
            if (!string.Equals(principal.FindFirst("sub")?.Value, configured.Subject, StringComparison.Ordinal))
                return ServiceTokenValidationResult.Failure("invalid_service_token");

            return ServiceTokenValidationResult.Success(new AuthenticatedServiceContext(
                configured.Subject, configured.Issuer, configured.Audience, parsed.Id,
                issuedAt, expiresAt, parsed.Header.Kid));
        }
        catch (SecurityTokenException)
        {
            return ServiceTokenValidationResult.Failure("invalid_service_token");
        }
    }
}

/// <summary>Protects the internal Live Feed decision endpoint.</summary>
public sealed class LiveFeedServiceAuthenticationFilter(
    ILiveFeedServiceTokenValidator validator) : IEndpointFilter
{
    /// <inheritdoc />
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var header = context.HttpContext.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return Results.Unauthorized();

        var result = validator.Validate(header["Bearer ".Length..].Trim());
        return result.IsValid ? await next(context) : Results.Unauthorized();
    }
}
