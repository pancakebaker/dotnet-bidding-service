using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using bidding_service.Data;
using bidding_service.Domain;
using bidding_service.Security.ClientAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace bidding_service.Tests;

[Collection("Bidding service database")]
public sealed class ClientAssertionValidatorTests : IClassFixture<AuctionApiFactory>
{
    private static readonly Guid ApplicationId = Guid.Parse("cccccccc-4444-4444-8444-444444444444");
    private static readonly Guid TenantId = Guid.Parse("aaaaaaaa-2222-4222-8222-222222222222");
    private static readonly Guid OtherTenantId = Guid.Parse("bbbbbbbb-3333-4333-8333-333333333333");
    private const string ClientId = "validator-test-client";
    private const string KeyId = "validator-key-01";
    private readonly AuctionApiFactory factory;

    public ClientAssertionValidatorTests(AuctionApiFactory factory) => this.factory = factory;

    [Fact]
    public async Task ValidAssertionReturnsAdmittedApplicationIdentity()
    {
        await factory.ResetDatabaseAsync();
        using var signingKey = RSA.Create(2048);
        var credential = await SeedAsync(signingKey);
        var assertion = CreateAssertion(signingKey, tenantId: TenantId);

        var result = await ValidateAsync(assertion);

        Assert.True(result.IsValid);
        Assert.Equal(ApplicationId, result.Identity?.ClientApplicationId);
        Assert.Equal(ClientId, result.Identity?.ClientId);
        Assert.Equal(TenantId, result.Identity?.TenantId);
        Assert.Equal(credential.Id, result.Identity?.ClientCredentialId);
        Assert.Equal(KeyId, result.Identity?.KeyId);
        Assert.Equal("assertion-jti", result.Identity?.Jti);
    }

    [Theory]
    [InlineData(ClientAssertionFailureReason.InvalidSignature)]
    [InlineData(ClientAssertionFailureReason.InvalidAudience)]
    [InlineData(ClientAssertionFailureReason.TenantMismatch)]
    public async Task InvalidSignatureAudienceOrTenantIsRejected(ClientAssertionFailureReason expected)
    {
        await factory.ResetDatabaseAsync();
        using var signingKey = RSA.Create(2048);
        await SeedAsync(signingKey);
        using var otherKey = RSA.Create(2048);
        var assertion = CreateAssertion(
            expected == ClientAssertionFailureReason.InvalidSignature ? otherKey : signingKey,
            audience: expected == ClientAssertionFailureReason.InvalidAudience ? "wrong-audience" : "dbap-bidding-service",
            tenantId: expected == ClientAssertionFailureReason.TenantMismatch ? OtherTenantId : TenantId);

        var result = await ValidateAsync(assertion);

        Assert.False(result.IsValid);
        Assert.Equal(expected, result.FailureReason);
    }

    [Fact]
    public async Task UnknownApplicationAndCredentialAreRejected()
    {
        await factory.ResetDatabaseAsync();
        using var signingKey = RSA.Create(2048);
        await SeedAsync(signingKey);

        var unknownIssuer = await ValidateAsync(CreateAssertion(signingKey, issuer: "unknown-client"));
        var unknownKid = await ValidateAsync(CreateAssertion(signingKey, keyId: "unknown-key"));

        Assert.Equal(ClientAssertionFailureReason.UnknownApplication, unknownIssuer.FailureReason);
        Assert.Equal(ClientAssertionFailureReason.UnknownCredential, unknownKid.FailureReason);
    }

    [Theory]
    [InlineData(ClientApplicationStatus.Disabled, ClientAssertionFailureReason.ApplicationDisabled)]
    [InlineData(ClientApplicationStatus.Revoked, ClientAssertionFailureReason.ApplicationRevoked)]
    public async Task InactiveApplicationIsRejected(ClientApplicationStatus status, ClientAssertionFailureReason expected)
    {
        await factory.ResetDatabaseAsync();
        using var signingKey = RSA.Create(2048);
        await SeedAsync(signingKey, status);

        var result = await ValidateAsync(CreateAssertion(signingKey));

        Assert.Equal(expected, result.FailureReason);
    }

    [Fact]
    public async Task RevokedOrNotYetValidCredentialIsRejected()
    {
        await factory.ResetDatabaseAsync();
        using var revokedKey = RSA.Create(2048);
        var revoked = await SeedAsync(revokedKey);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BiddingDbContext>();
            db.ClientCredentials.Single(item => item.Id == revoked.Id).Revoke(TestAuctionData.Now);
            await db.SaveChangesAsync();
        }

        var revokedResult = await ValidateAsync(CreateAssertion(revokedKey));

        await factory.ResetDatabaseAsync();
        using var futureKey = RSA.Create(2048);
        await SeedAsync(futureKey, validFrom: TestAuctionData.Now.AddMinutes(5));
        var futureResult = await ValidateAsync(CreateAssertion(futureKey));

        Assert.Equal(ClientAssertionFailureReason.CredentialRevoked, revokedResult.FailureReason);
        Assert.Equal(ClientAssertionFailureReason.CredentialNotYetValid, futureResult.FailureReason);
    }

    [Theory]
    [InlineData("missing", null, null, null)]
    [InlineData("nbf", "dbap-bidding-service", "missing", null)]
    [InlineData("exp", "dbap-bidding-service", "present", "missing")]
    public async Task RequiredClaimsAreEnforced(string omitted, string? audience, string? nbf, string? exp)
    {
        await factory.ResetDatabaseAsync();
        using var signingKey = RSA.Create(2048);
        await SeedAsync(signingKey);
        var assertion = CreateAssertion(
            signingKey,
            audience: audience ?? "dbap-bidding-service",
            omitJti: omitted == "missing",
            omitNbf: nbf == "missing",
            omitExp: exp == "missing");

        var result = await ValidateAsync(assertion);

        Assert.False(result.IsValid);
        Assert.NotNull(result.FailureReason);
    }

    [Fact]
    public async Task UnsupportedAlgorithmAndExcessiveLifetimeAreRejected()
    {
        await factory.ResetDatabaseAsync();
        using var signingKey = RSA.Create(2048);
        await SeedAsync(signingKey);
        var excessive = await ValidateAsync(CreateAssertion(signingKey, lifetime: TimeSpan.FromMinutes(5)));
        var unsupported = await ValidateAsync(CreateHmacAssertion());

        Assert.Equal(ClientAssertionFailureReason.InvalidTimeWindow, excessive.FailureReason);
        Assert.Equal(ClientAssertionFailureReason.UnsupportedAlgorithm, unsupported.FailureReason);
    }

    [Fact]
    public async Task RequiredHeaderAndIdentityClaimsAreEnforced()
    {
        await factory.ResetDatabaseAsync();
        using var signingKey = RSA.Create(2048);
        await SeedAsync(signingKey);

        var missingKid = await ValidateAsync(CreateAssertion(signingKey, keyId: null));
        var missingTenant = await ValidateAsync(CreateAssertion(signingKey, omitTenant: true));
        var missingIssuedAt = await ValidateAsync(CreateAssertion(signingKey, omitIat: true));
        var malformed = await ValidateAsync("not-a-jwt");

        Assert.Equal(ClientAssertionFailureReason.MissingKeyId, missingKid.FailureReason);
        Assert.Equal(ClientAssertionFailureReason.MissingTenant, missingTenant.FailureReason);
        Assert.Equal(ClientAssertionFailureReason.MissingIssuedAt, missingIssuedAt.FailureReason);
        Assert.Equal(ClientAssertionFailureReason.MalformedToken, malformed.FailureReason);
    }

    [Fact]
    public async Task CredentialAndApplicationOwnershipMustMatch()
    {
        await factory.ResetDatabaseAsync();
        using var signingKey = RSA.Create(2048);
        var credential = await SeedAsync(signingKey);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BiddingDbContext>();
            db.ClientApplications.Add(ClientApplication.Create(
                Guid.Parse("dddddddd-5555-4555-8555-555555555555"),
                "second-client",
                TenantId,
                "Second client",
                ClientApplicationStatus.Active,
                TestAuctionData.Now));
            await db.SaveChangesAsync();
        }

        var result = await ValidateAsync(CreateAssertion(signingKey, issuer: "second-client"));

        Assert.Equal(credential.ClientApplicationId, ApplicationId);
        Assert.Equal(ClientAssertionFailureReason.CredentialMismatch, result.FailureReason);
    }

    [Fact]
    public async Task ReplayIsNotPersistedInThisPhase()
    {
        await factory.ResetDatabaseAsync();
        using var signingKey = RSA.Create(2048);
        await SeedAsync(signingKey);
        var assertion = CreateAssertion(signingKey);

        var first = await ValidateAsync(assertion);
        var second = await ValidateAsync(assertion);

        Assert.True(first.IsValid);
        Assert.True(second.IsValid);
    }

    private async Task<ClientCredential> SeedAsync(
        RSA signingKey,
        ClientApplicationStatus status = ClientApplicationStatus.Active,
        DateTimeOffset? validFrom = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BiddingDbContext>();
        db.Tenants.Add(Tenant.Create(TenantId, "Validator Tenant", TenantStatus.Active, TestAuctionData.Now));
        db.ClientApplications.Add(ClientApplication.Create(
            ApplicationId,
            ClientId,
            TenantId,
            "Validator Test Client",
            status,
            TestAuctionData.Now));
        var credential = ClientCredential.Create(
            Guid.Parse("eeeeeeee-6666-4666-8666-666666666666"),
            ApplicationId,
            KeyId,
            signingKey.ExportSubjectPublicKeyInfoPem(),
            validFrom ?? TestAuctionData.Now.AddMinutes(-1),
            TestAuctionData.Now.AddMinutes(10),
            TestAuctionData.Now);
        db.ClientCredentials.Add(credential);
        await db.SaveChangesAsync();
        return credential;
    }

    private async Task<ClientAssertionValidationResult> ValidateAsync(string assertion)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<IClientAssertionValidator>()
            .ValidateAsync(assertion);
    }

    private static string CreateAssertion(
        RSA signingKey,
        string issuer = ClientId,
        string audience = "dbap-bidding-service",
        Guid tenantId = default,
        string? keyId = KeyId,
        TimeSpan? lifetime = null,
        bool omitJti = false,
        bool omitNbf = false,
        bool omitExp = false,
        bool omitIat = false,
        bool omitTenant = false)
    {
        var now = TestAuctionData.Now;
        var payload = new JwtPayload
        {
            [JwtRegisteredClaimNames.Iss] = issuer,
            [JwtRegisteredClaimNames.Aud] = audience,
            [JwtRegisteredClaimNames.Exp] = now.Add(lifetime ?? TimeSpan.FromSeconds(30)).ToUnixTimeSeconds(),
        };
        if (!omitIat)
            payload[JwtRegisteredClaimNames.Iat] = now.ToUnixTimeSeconds();
        if (!omitTenant)
            payload["tenant_id"] = (tenantId == default ? TenantId : tenantId).ToString();
        if (!omitJti)
            payload[JwtRegisteredClaimNames.Jti] = "assertion-jti";
        if (!omitNbf)
            payload[JwtRegisteredClaimNames.Nbf] = now.ToUnixTimeSeconds();
        if (omitExp)
            payload.Remove(JwtRegisteredClaimNames.Exp);

        var rsaKey = new RsaSecurityKey(signingKey) { KeyId = keyId };
        var header = new JwtHeader(new SigningCredentials(rsaKey, SecurityAlgorithms.RsaSha256));
        if (keyId is null)
            header.Remove(JwtHeaderParameterNames.Kid);
        header[JwtHeaderParameterNames.Typ] = "JWT";
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(header, payload));
    }

    private static string CreateHmacAssertion()
    {
        var now = TestAuctionData.Now;
        var payload = new JwtPayload
        {
            [JwtRegisteredClaimNames.Iss] = ClientId,
            [JwtRegisteredClaimNames.Aud] = "dbap-bidding-service",
            [JwtRegisteredClaimNames.Iat] = now.ToUnixTimeSeconds(),
            [JwtRegisteredClaimNames.Nbf] = now.ToUnixTimeSeconds(),
            [JwtRegisteredClaimNames.Exp] = now.AddSeconds(30).ToUnixTimeSeconds(),
            [JwtRegisteredClaimNames.Jti] = "assertion-jti",
            ["tenant_id"] = TenantId.ToString()
        };
        var header = new JwtHeader(new SigningCredentials(
            new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes("test-hmac-secret-test-hmac-secret")),
            SecurityAlgorithms.HmacSha256));
        header[JwtHeaderParameterNames.Typ] = "JWT";
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(header, payload));
    }
}
