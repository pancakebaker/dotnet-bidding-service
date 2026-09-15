// <copyright file="ClientAssertionAdmission.cs" company="Distributed Bidding Auction Platform">
// Copyright (c) Distributed Bidding Auction Platform. Licensed under the MIT license.
// </copyright>
using bidding_service.Options;
using bidding_service.Services;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace bidding_service.Security.ClientAssertions;

/// <summary>Application identity attached to the current HTTP request.</summary>
public sealed record AuthenticatedClientContext(
    Guid ClientApplicationId,
    string ClientId,
    Guid TenantId,
    Guid ClientCredentialId,
    string KeyId,
    string Jti);

/// <summary>Reads the admitted application identity for the current request.</summary>
public static class ClientAssertionRequestContext
{
    private static readonly object Key = new();

    /// <summary>Stores an admitted application identity on the request.</summary>
    public static void Set(HttpContext httpContext, AuthenticatedClientContext context) =>
        httpContext.Items[Key] = context;

    /// <summary>Tries to read the admitted application identity from the request.</summary>
    public static bool TryGet(HttpContext httpContext, out AuthenticatedClientContext? context)
    {
        context = httpContext.Items[Key] as AuthenticatedClientContext;
        return context is not null;
    }
}

/// <summary>Result of combining client assertion and bearer tenant authentication.</summary>
public sealed record ClientAssertionAdmissionResult(
    AuthenticatedClientContext? Context,
    int StatusCode,
    string ErrorCode)
{
    /// <summary>Gets whether admission succeeded.</summary>
    public bool IsSuccess => Context is not null;

    /// <summary>Creates a successful result.</summary>
    public static ClientAssertionAdmissionResult Success(AuthenticatedClientContext context) =>
        new(context, StatusCodes.Status200OK, string.Empty);

    /// <summary>Creates a controlled failure result.</summary>
    public static ClientAssertionAdmissionResult Failure(int statusCode, string errorCode) =>
        new(null, statusCode, errorCode);
}

/// <summary>Composes client assertion authentication with bearer tenant agreement.</summary>
public interface IClientAssertionAdmissionService
{
    /// <summary>Authenticates and binds the application to the request tenant.</summary>
    Task<ClientAssertionAdmissionResult> AuthenticateAsync(
        HttpContext httpContext,
        CancellationToken cancellationToken = default);
}

/// <summary>Central admission service used by tenant-facing auction endpoints.</summary>
public sealed class ClientAssertionAdmissionService(
    IClientAssertionAuthenticator authenticator,
    ITenantIdentityAccessor tenantIdentityAccessor,
    IOptions<ClientAssertionAdmissionOptions> options) : IClientAssertionAdmissionService
{
    private const string HeaderName = "X-Client-Assertion";

    /// <inheritdoc />
    public async Task<ClientAssertionAdmissionResult> AuthenticateAsync(
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        if (!options.Value.Enabled)
        {
            return ClientAssertionAdmissionResult.Success(new AuthenticatedClientContext(
                Guid.Empty,
                string.Empty,
                Guid.Empty,
                Guid.Empty,
                string.Empty,
                string.Empty));
        }

        if (!httpContext.Request.Headers.TryGetValue(HeaderName, out var values)
            || values.Count != 1
            || string.IsNullOrWhiteSpace(values[0]))
        {
            return ClientAssertionAdmissionResult.Failure(
                StatusCodes.Status401Unauthorized,
                "invalid_client_assertion");
        }

        var authentication = await authenticator.AuthenticateAsync(values[0]!, cancellationToken);
        if (!authentication.IsValid || authentication.Identity is null)
        {
            return authentication.ReplayOutcome == ClientAssertionReplayConsumeOutcome.ReplayStoreUnavailable
                ? ClientAssertionAdmissionResult.Failure(
                    StatusCodes.Status503ServiceUnavailable,
                    "client_assertion_unavailable")
                : ClientAssertionAdmissionResult.Failure(
                    StatusCodes.Status401Unauthorized,
                    "invalid_client_assertion");
        }

        if (!tenantIdentityAccessor.TryGetTenantId(httpContext.User, out var bearerTenantId)
            || bearerTenantId != authentication.Identity.TenantId)
        {
            return ClientAssertionAdmissionResult.Failure(
                StatusCodes.Status403Forbidden,
                "tenant_identity_mismatch");
        }

        var context = new AuthenticatedClientContext(
            authentication.Identity.ClientApplicationId,
            authentication.Identity.ClientId,
            authentication.Identity.TenantId,
            authentication.Identity.ClientCredentialId,
            authentication.Identity.KeyId,
            authentication.Identity.Jti);
        return ClientAssertionAdmissionResult.Success(context);
    }
}

/// <summary>Endpoint filter enforcing application admission for auction APIs.</summary>
public sealed class ClientAssertionAdmissionFilter(
    IOptions<ClientAssertionAdmissionOptions> options) : IEndpointFilter
{
    /// <inheritdoc />
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        if (!options.Value.Enabled)
        {
            return await next(context);
        }

        if (ClientAssertionRequestContext.TryGet(context.HttpContext, out _))
        {
            return await next(context);
        }

        ClientAssertionAdmissionResult admission;
        try
        {
            var admissionService = context.HttpContext.RequestServices
                .GetRequiredService<IClientAssertionAdmissionService>();
            admission = await admissionService.AuthenticateAsync(
                context.HttpContext,
                context.HttpContext.RequestAborted);
        }
        catch (Exception exception) when (
            exception is RedisException or TimeoutException or InvalidOperationException)
        {
            admission = ClientAssertionAdmissionResult.Failure(
                StatusCodes.Status503ServiceUnavailable,
                "client_assertion_unavailable");
        }
        if (!admission.IsSuccess)
        {
            return Results.Json(
                new { code = admission.ErrorCode, message = "Client assertion admission failed." },
                statusCode: admission.StatusCode);
        }

        if (admission.Context is not null && admission.Context.ClientApplicationId != Guid.Empty)
        {
            ClientAssertionRequestContext.Set(context.HttpContext, admission.Context);
        }

        return await next(context);
    }
}
