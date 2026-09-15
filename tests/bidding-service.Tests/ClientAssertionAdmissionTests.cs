using System.Security.Claims;
using bidding_service.Options;
using bidding_service.Security.ClientAssertions;
using bidding_service.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace bidding_service.Tests;

public sealed class ClientAssertionAdmissionTests
{
    private static readonly Guid TenantId = Guid.Parse("aaaaaaaa-2222-4222-8222-222222222222");
    private static readonly ValidatedClientIdentity Identity = new(
        Guid.Parse("cccccccc-4444-4444-8444-444444444444"),
        "local-laravel-client",
        TenantId,
        Guid.Parse("dddddddd-5555-4555-8555-555555555555"),
        "local-laravel-key-01",
        "jti-1",
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow.AddSeconds(30),
        "fingerprint");

    [Fact]
    public async Task DisabledAdmissionDoesNotReadOrConsumeAssertion()
    {
        var authenticator = new FakeAuthenticator(ClientAssertionAuthenticationResult.Success(Identity));
        var service = CreateService(authenticator, TenantId, enabled: false);
        var context = CreateHttpContext(TenantId);

        var result = await service.AuthenticateAsync(context);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, authenticator.Calls);
    }

    [Fact]
    public async Task MissingEmptyAndDuplicateHeadersAreUnauthorized()
    {
        var authenticator = new FakeAuthenticator(ClientAssertionAuthenticationResult.Success(Identity));
        var service = CreateService(authenticator, TenantId);

        var missing = await service.AuthenticateAsync(CreateHttpContext(TenantId));
        var emptyContext = CreateHttpContext(TenantId);
        emptyContext.Request.Headers.Append("X-Client-Assertion", " ");
        var empty = await service.AuthenticateAsync(emptyContext);
        var duplicateContext = CreateHttpContext(TenantId);
        duplicateContext.Request.Headers.Append("X-Client-Assertion", "one");
        duplicateContext.Request.Headers.Append("X-Client-Assertion", "two");
        var duplicate = await service.AuthenticateAsync(duplicateContext);

        Assert.All([missing, empty, duplicate], result =>
        {
            Assert.False(result.IsSuccess);
            Assert.Equal(401, result.StatusCode);
            Assert.Equal("invalid_client_assertion", result.ErrorCode);
        });
        Assert.Equal(0, authenticator.Calls);
    }

    [Fact]
    public async Task ValidAssertionMustAgreeWithBearerTenant()
    {
        var authenticator = new FakeAuthenticator(ClientAssertionAuthenticationResult.Success(Identity));
        var service = CreateService(authenticator, Guid.Parse("bbbbbbbb-3333-4333-8333-333333333333"));
        var context = CreateHttpContext(Guid.Parse("bbbbbbbb-3333-4333-8333-333333333333"));
        context.Request.Headers.Append("X-Client-Assertion", "valid");

        var result = await service.AuthenticateAsync(context);

        Assert.False(result.IsSuccess);
        Assert.Equal(403, result.StatusCode);
        Assert.Equal("tenant_identity_mismatch", result.ErrorCode);
        Assert.Equal(1, authenticator.Calls);
    }

    [Theory]
    [InlineData(ClientAssertionReplayConsumeOutcome.ReplayDetected, 401, "invalid_client_assertion")]
    [InlineData(ClientAssertionReplayConsumeOutcome.ReplayStoreUnavailable, 503, "client_assertion_unavailable")]
    public async Task AuthenticatorFailuresMapToControlledResponses(
        ClientAssertionReplayConsumeOutcome outcome,
        int expectedStatus,
        string expectedCode)
    {
        var authenticator = new FakeAuthenticator(
            ClientAssertionAuthenticationResult.ReplayFailure(outcome));
        var service = CreateService(authenticator, TenantId);
        var context = CreateHttpContext(TenantId);
        context.Request.Headers.Append("X-Client-Assertion", "invalid");

        var result = await service.AuthenticateAsync(context);

        Assert.False(result.IsSuccess);
        Assert.Equal(expectedStatus, result.StatusCode);
        Assert.Equal(expectedCode, result.ErrorCode);
    }

    [Fact]
    public async Task ValidAssertionProducesTypedApplicationContext()
    {
        var authenticator = new FakeAuthenticator(ClientAssertionAuthenticationResult.Success(Identity));
        var service = CreateService(authenticator, TenantId);
        var context = CreateHttpContext(TenantId);
        context.Request.Headers.Append("X-Client-Assertion", "valid");

        var result = await service.AuthenticateAsync(context);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Context);
        Assert.Equal(Identity.ClientApplicationId, result.Context.ClientApplicationId);
        Assert.Equal(Identity.ClientId, result.Context.ClientId);
        Assert.Equal(Identity.TenantId, result.Context.TenantId);
        Assert.Equal(Identity.KeyId, result.Context.KeyId);
        Assert.Equal(1, authenticator.Calls);
    }

    [Fact]
    public async Task AdmissionFilterAuthenticatesAtMostOncePerRequest()
    {
        var admission = new FakeAdmissionService(
            ClientAssertionAdmissionResult.Success(new AuthenticatedClientContext(
                Identity.ClientApplicationId,
                Identity.ClientId,
                Identity.TenantId,
                Identity.ClientCredentialId,
                Identity.KeyId,
                Identity.Jti)));
        var services = new ServiceCollection()
            .AddSingleton<IClientAssertionAdmissionService>(admission)
            .BuildServiceProvider();
        var context = new TestInvocationContext(CreateHttpContext(TenantId));
        context.HttpContext.RequestServices = services;
        var filter = new ClientAssertionAdmissionFilter(
            Microsoft.Extensions.Options.Options.Create(new ClientAssertionAdmissionOptions { Enabled = true }));
        var nextCalls = 0;
        EndpointFilterDelegate next = _ =>
        {
            nextCalls++;
            return ValueTask.FromResult<object?>("executed");
        };

        await filter.InvokeAsync(context, next);
        await filter.InvokeAsync(context, next);

        Assert.Equal(1, admission.Calls);
        Assert.Equal(2, nextCalls);
        Assert.True(ClientAssertionRequestContext.TryGet(context.HttpContext, out var admitted));
        Assert.Equal(Identity.ClientId, admitted!.ClientId);
    }

    [Fact]
    public async Task DisabledAdmissionFilterDoesNotResolveAdmissionService()
    {
        var filter = new ClientAssertionAdmissionFilter(
            Microsoft.Extensions.Options.Options.Create(new ClientAssertionAdmissionOptions { Enabled = false }));
        var invocation = new TestInvocationContext(CreateHttpContext(TenantId));
        var nextCalls = 0;
        EndpointFilterDelegate next = _ =>
        {
            nextCalls++;
            return ValueTask.FromResult<object?>("executed");
        };

        var result = await filter.InvokeAsync(invocation, next);

        Assert.Equal("executed", result);
        Assert.Equal(1, nextCalls);
    }

    private static ClientAssertionAdmissionService CreateService(
        FakeAuthenticator authenticator,
        Guid bearerTenantId,
        bool enabled = true) =>
        new(
            authenticator,
            new FakeTenantIdentityAccessor(bearerTenantId),
            Microsoft.Extensions.Options.Options.Create(new ClientAssertionAdmissionOptions { Enabled = enabled }));

    private static DefaultHttpContext CreateHttpContext(Guid tenantId)
    {
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("tenant_id", tenantId.ToString())
        ], "test"));
        return context;
    }

    private sealed class FakeAuthenticator(ClientAssertionAuthenticationResult result)
        : IClientAssertionAuthenticator
    {
        public int Calls { get; private set; }

        public Task<ClientAssertionAuthenticationResult> AuthenticateAsync(
            string assertion,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    private sealed class FakeTenantIdentityAccessor(Guid tenantId) : ITenantIdentityAccessor
    {
        public bool TryGetTenantId(ClaimsPrincipal principal, out Guid resolvedTenantId)
        {
            resolvedTenantId = tenantId;
            return tenantId != Guid.Empty;
        }
    }

    private sealed class FakeAdmissionService(ClientAssertionAdmissionResult result)
        : IClientAssertionAdmissionService
    {
        public int Calls { get; private set; }

        public Task<ClientAssertionAdmissionResult> AuthenticateAsync(
            HttpContext httpContext,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    private sealed class TestInvocationContext(HttpContext httpContext) : EndpointFilterInvocationContext
    {
        public override HttpContext HttpContext { get; } = httpContext;

        public override IList<object?> Arguments { get; } = [];

        public override T GetArgument<T>(int index) => (T)Arguments[index]!;
    }
}
