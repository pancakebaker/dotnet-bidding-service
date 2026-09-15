using System.Security.Claims;
using bidding_service.Services;

namespace bidding_service.Tests;

public sealed class TenantIdentityAccessorTests
{
    private readonly TenantIdentityAccessor accessor = new();

    [Fact]
    public void Resolves_a_valid_canonical_tenant_claim()
    {
        var tenantId = Guid.Parse("aaaaaaaa-1111-4111-8111-111111111111");
        var principal = Principal(("tenant_id", tenantId.ToString()));

        Assert.True(accessor.TryGetTenantId(principal, out var resolved));
        Assert.Equal(tenantId, resolved);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-uuid")]
    public void Rejects_missing_or_malformed_tenant_claim(string? value)
    {
        var principal = value is null
            ? Principal()
            : Principal(("tenant_id", value));

        Assert.False(accessor.TryGetTenantId(principal, out _));
    }

    [Fact]
    public void Rejects_noncanonical_tenant_claim_alias()
    {
        var principal = Principal(("tenantId", "aaaaaaaa-1111-4111-8111-111111111111"));

        Assert.False(accessor.TryGetTenantId(principal, out _));
    }

    private static ClaimsPrincipal Principal(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(claim => new Claim(claim.Type, claim.Value)), "test"));
}
