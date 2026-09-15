// <copyright file="ClientAssertionReplayProtection.cs" company="Distributed Bidding Auction Platform">
// Copyright (c) Distributed Bidding Auction Platform. Licensed under the MIT license.
// </copyright>
using System.Security.Cryptography;
using System.Text;
using StackExchange.Redis;

namespace bidding_service.Security.ClientAssertions;

/// <summary>Outcomes of attempting to consume one client assertion.</summary>
public enum ClientAssertionReplayConsumeOutcome
{
    /// <summary>The assertion JTI was recorded successfully.</summary>
    Consumed,
    /// <summary>The assertion JTI was already recorded.</summary>
    ReplayDetected,
    /// <summary>The replay store could not be reached or used.</summary>
    ReplayStoreUnavailable,
    /// <summary>The assertion expiry could not produce a safe replay TTL.</summary>
    InvalidExpiry
}

/// <summary>Result of an atomic client assertion replay consumption attempt.</summary>
public sealed record ClientAssertionReplayConsumeResult(ClientAssertionReplayConsumeOutcome Outcome)
{
    /// <summary>Gets whether this attempt consumed the assertion.</summary>
    public bool IsConsumed => Outcome == ClientAssertionReplayConsumeOutcome.Consumed;
}

/// <summary>Consumes a validated client assertion identity at most once.</summary>
public interface IClientAssertionReplayProtector
{
    /// <summary>Atomically consumes one validated assertion identity.</summary>
    Task<ClientAssertionReplayConsumeResult> TryConsumeAsync(
        ValidatedClientIdentity identity,
        CancellationToken cancellationToken = default);
}

/// <summary>Minimal Redis port used by the replay protector.</summary>
public interface IClientAssertionReplayStore
{
    /// <summary>Atomically creates a replay record when the key does not exist.</summary>
    Task<bool> TryCreateAsync(string key, TimeSpan expiry, CancellationToken cancellationToken);
}

/// <summary>Redis implementation using an atomic SET NX with a bounded expiry.</summary>
public sealed class RedisClientAssertionReplayProtector(
    IClientAssertionReplayStore store,
    TimeProvider timeProvider) : IClientAssertionReplayProtector
{
    private const string KeyPrefix = "dbap:bidding:client-assertion:replay:";
    private static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaximumReplayLifetime = TimeSpan.FromMinutes(2);

    /// <inheritdoc />
    public async Task<ClientAssertionReplayConsumeResult> TryConsumeAsync(
        ValidatedClientIdentity identity,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var expiry = identity.ExpiresAtUtc - now + ClockSkew;
        if (expiry <= TimeSpan.Zero || expiry > MaximumReplayLifetime)
        {
            return new(ClientAssertionReplayConsumeOutcome.InvalidExpiry);
        }

        try
        {
            var consumed = await store.TryCreateAsync(
                BuildKey(identity),
                expiry,
                cancellationToken);
            return new(
                consumed
                    ? ClientAssertionReplayConsumeOutcome.Consumed
                    : ClientAssertionReplayConsumeOutcome.ReplayDetected);
        }
        catch (Exception exception) when (exception is RedisException or TimeoutException)
        {
            return new(ClientAssertionReplayConsumeOutcome.ReplayStoreUnavailable);
        }
    }

    /// <summary>Builds a bounded key from the validated application identity.</summary>
    public static string BuildKey(ValidatedClientIdentity identity)
    {
        var value = $"{identity.TenantId:D}:{identity.ClientId}:{identity.Jti}";
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
        return KeyPrefix + digest;
    }
}

/// <summary>StackExchange.Redis adapter for atomic replay-record creation.</summary>
public sealed class StackExchangeRedisClientAssertionReplayStore(IDatabase database)
    : IClientAssertionReplayStore
{
    /// <inheritdoc />
    public async Task<bool> TryCreateAsync(
        string key,
        TimeSpan expiry,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await database.StringSetAsync(key, "1", expiry, When.NotExists);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }
}

/// <summary>Result of cryptographic validation followed by replay consumption.</summary>
public sealed record ClientAssertionAuthenticationResult(
    ValidatedClientIdentity? Identity,
    ClientAssertionFailureReason? AssertionFailureReason,
    ClientAssertionReplayConsumeOutcome? ReplayOutcome)
{
    /// <summary>Gets whether validation and replay consumption both succeeded.</summary>
    public bool IsValid => Identity is not null && AssertionFailureReason is null && ReplayOutcome is null;

    /// <summary>Creates a result for a cryptographic assertion failure.</summary>
    public static ClientAssertionAuthenticationResult Invalid(ClientAssertionFailureReason reason) =>
        new(null, reason, null);

    /// <summary>Creates a result for a replay-protection failure.</summary>
    public static ClientAssertionAuthenticationResult ReplayFailure(ClientAssertionReplayConsumeOutcome outcome) =>
        new(null, null, outcome);

    /// <summary>Creates a successful authentication result.</summary>
    public static ClientAssertionAuthenticationResult Success(ValidatedClientIdentity identity) =>
        new(identity, null, null);
}

/// <summary>Composes cryptographic validation with one-time replay consumption.</summary>
public interface IClientAssertionAuthenticator
{
    /// <summary>Validates and consumes one client assertion.</summary>
    Task<ClientAssertionAuthenticationResult> AuthenticateAsync(
        string assertion,
        CancellationToken cancellationToken = default);
}

/// <summary>Reusable MT5.3b composition boundary; not wired to HTTP endpoints yet.</summary>
public sealed class ClientAssertionAuthenticator(
    IClientAssertionValidator validator,
    IClientAssertionReplayProtector replayProtector) : IClientAssertionAuthenticator
{
    /// <inheritdoc />
    public async Task<ClientAssertionAuthenticationResult> AuthenticateAsync(
        string assertion,
        CancellationToken cancellationToken = default)
    {
        var validation = await validator.ValidateAsync(assertion, cancellationToken);
        if (!validation.IsValid || validation.Identity is null)
        {
            return ClientAssertionAuthenticationResult.Invalid(
                validation.FailureReason ?? ClientAssertionFailureReason.InvalidTokenClaims);
        }

        var replay = await replayProtector.TryConsumeAsync(validation.Identity, cancellationToken);
        return replay.IsConsumed
            ? ClientAssertionAuthenticationResult.Success(validation.Identity)
            : ClientAssertionAuthenticationResult.ReplayFailure(replay.Outcome);
    }
}
