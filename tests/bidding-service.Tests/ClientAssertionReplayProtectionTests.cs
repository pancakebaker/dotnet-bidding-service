using bidding_service.Security.ClientAssertions;
namespace bidding_service.Tests;

public sealed class ClientAssertionReplayProtectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-2222-4222-8222-222222222222");
    private readonly FixedTimeProvider timeProvider = new(Now);

    [Fact]
    public async Task FirstUseSucceedsAndSecondUseIsReplay()
    {
        var store = new FakeReplayStore();
        var protector = CreateProtector(store);

        var first = await protector.TryConsumeAsync(Identity());
        var second = await protector.TryConsumeAsync(Identity());

        Assert.Equal(ClientAssertionReplayConsumeOutcome.Consumed, first.Outcome);
        Assert.Equal(ClientAssertionReplayConsumeOutcome.ReplayDetected, second.Outcome);
    }

    [Fact]
    public async Task DifferentJtisAreIndependentlyConsumable()
    {
        var store = new FakeReplayStore();
        var protector = CreateProtector(store);

        var first = await protector.TryConsumeAsync(Identity(jti: "jti-a"));
        var second = await protector.TryConsumeAsync(Identity(jti: "jti-b"));

        Assert.True(first.IsConsumed);
        Assert.True(second.IsConsumed);
    }

    [Fact]
    public async Task SameJtiDifferentClientsAreIndependentlyConsumable()
    {
        var store = new FakeReplayStore();
        var protector = CreateProtector(store);

        var first = await protector.TryConsumeAsync(Identity(clientId: "client-a"));
        var second = await protector.TryConsumeAsync(Identity(clientId: "client-b"));

        Assert.True(first.IsConsumed);
        Assert.True(second.IsConsumed);
    }

    [Fact]
    public async Task SameClientAndJtiReplayAcrossCredentials()
    {
        var store = new FakeReplayStore();
        var protector = CreateProtector(store);

        var first = await protector.TryConsumeAsync(Identity(credentialId: Guid.NewGuid(), keyId: "old-key"));
        var second = await protector.TryConsumeAsync(Identity(credentialId: Guid.NewGuid(), keyId: "new-key"));

        Assert.True(first.IsConsumed);
        Assert.Equal(ClientAssertionReplayConsumeOutcome.ReplayDetected, second.Outcome);
    }

    [Fact]
    public async Task ReplayTtlCoversAssertionAcceptanceWindowAndIsBounded()
    {
        var store = new FakeReplayStore();
        var protector = CreateProtector(store);

        var result = await protector.TryConsumeAsync(Identity(expiresAtUtc: Now.AddSeconds(40)));

        Assert.True(result.IsConsumed);
        Assert.Equal(TimeSpan.FromSeconds(70), store.LastExpiry);
        Assert.InRange(store.LastExpiry, TimeSpan.Zero, TimeSpan.FromMinutes(2));
    }

    [Fact]
    public async Task InvalidExpiryFailsBeforeStoreUse()
    {
        var store = new FakeReplayStore();
        var protector = CreateProtector(store);

        var result = await protector.TryConsumeAsync(Identity(expiresAtUtc: Now.AddMinutes(3)));

        Assert.Equal(ClientAssertionReplayConsumeOutcome.InvalidExpiry, result.Outcome);
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task RedisFailureFailsClosed()
    {
        var store = new FakeReplayStore { Exception = new TimeoutException("redis unavailable") };
        var protector = CreateProtector(store);

        var result = await protector.TryConsumeAsync(Identity());

        Assert.Equal(ClientAssertionReplayConsumeOutcome.ReplayStoreUnavailable, result.Outcome);
    }

    [Fact]
    public async Task CompositeAuthenticatorConsumesOnlyAfterSuccessfulValidation()
    {
        var store = new FakeReplayStore();
        var validator = new FakeValidator(ClientAssertionValidationResult.Success(Identity()));
        var authenticator = new ClientAssertionAuthenticator(validator, CreateProtector(store));

        var first = await authenticator.AuthenticateAsync("valid");
        var second = await authenticator.AuthenticateAsync("valid");

        Assert.True(first.IsValid);
        Assert.False(second.IsValid);
        Assert.Equal(ClientAssertionReplayConsumeOutcome.ReplayDetected, second.ReplayOutcome);
    }

    [Fact]
    public async Task InvalidAssertionDoesNotConsumeJti()
    {
        var store = new FakeReplayStore();
        var validator = new FakeValidator(ClientAssertionValidationResult.Failure(
            ClientAssertionFailureReason.InvalidSignature));
        var authenticator = new ClientAssertionAuthenticator(validator, CreateProtector(store));

        var result = await authenticator.AuthenticateAsync("invalid");

        Assert.False(result.IsValid);
        Assert.Equal(ClientAssertionFailureReason.InvalidSignature, result.AssertionFailureReason);
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task ReplayStoreFailureIsReturnedAsControlledFailure()
    {
        var store = new FakeReplayStore { Exception = new TimeoutException("redis unavailable") };
        var validator = new FakeValidator(ClientAssertionValidationResult.Success(Identity()));
        var authenticator = new ClientAssertionAuthenticator(validator, CreateProtector(store));

        var result = await authenticator.AuthenticateAsync("valid");

        Assert.False(result.IsValid);
        Assert.Equal(ClientAssertionReplayConsumeOutcome.ReplayStoreUnavailable, result.ReplayOutcome);
    }

    private RedisClientAssertionReplayProtector CreateProtector(FakeReplayStore store) =>
        new(store, timeProvider);

    private static ValidatedClientIdentity Identity(
        string clientId = "client-a",
        string jti = "same-jti",
        Guid? credentialId = null,
        string keyId = "key-a",
        Guid? tenantId = null,
        DateTimeOffset? expiresAtUtc = null) =>
        new(
            Guid.Parse("cccccccc-4444-4444-8444-444444444444"),
            clientId,
            tenantId ?? TenantA,
            credentialId ?? Guid.Parse("eeeeeeee-6666-4666-8666-666666666666"),
            keyId,
            jti,
            Now,
            expiresAtUtc ?? Now.AddSeconds(30),
            "fingerprint");

    private sealed class FakeReplayStore : IClientAssertionReplayStore
    {
        private readonly HashSet<string> keys = [];

        public int Calls { get; private set; }
        public TimeSpan LastExpiry { get; private set; }
        public Exception? Exception { get; init; }

        public Task<bool> TryCreateAsync(string key, TimeSpan expiry, CancellationToken cancellationToken)
        {
            Calls++;
            LastExpiry = expiry;
            if (Exception is not null) throw Exception;
            return Task.FromResult(keys.Add(key));
        }
    }

    private sealed class FakeValidator(ClientAssertionValidationResult result) : IClientAssertionValidator
    {
        public Task<ClientAssertionValidationResult> ValidateAsync(
            string assertion,
            CancellationToken cancellationToken = default) => Task.FromResult(result);
    }
}
