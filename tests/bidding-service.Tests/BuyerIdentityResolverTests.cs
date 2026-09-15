using System.Security.Claims;
using bidding_service.Services;
using Microsoft.AspNetCore.Http;

namespace bidding_service.Tests;

public sealed class BuyerIdentityResolverTests
{
    private readonly BuyerIdentityResolver _resolver = new();

    [Fact]
    public void UnauthenticatedDemoRequestUsesTrimmedRequestIdentity()
    {
        var context = new DefaultHttpContext();
        Assert.Equal("buyer-123", _resolver.Resolve(context, " buyer-123 "));
    }

    [Fact]
    public void AuthenticatedPrincipalOverridesCallerSuppliedIdentity()
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("sub", "principal-123")], "test"))
        };

        Assert.Equal("principal-123", _resolver.Resolve(context, "attacker-controlled-id"));
    }

    [Fact]
    public void AuthenticatedPrincipalWithoutIdentityDoesNotFallbackToRequest()
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity("test"))
        };

        Assert.Null(_resolver.Resolve(context, "attacker-controlled-id"));
    }
}
