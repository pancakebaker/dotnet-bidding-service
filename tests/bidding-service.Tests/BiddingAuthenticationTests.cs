using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;

namespace bidding_service.Tests;

[Collection("Bidding service database")]
public sealed class BiddingAuthenticationTests : IClassFixture<AuctionApiFactory>
{
    private readonly HttpClient client;

    public BiddingAuthenticationTests(AuctionApiFactory factory)
    {
        client = factory.CreateClient();
    }

    [Fact]
    public async Task ValidLaravelStyleTokenExposesAuthenticatedSubject()
    {
        var token = JwtTestKeys.CreateToken("stable-subject");
        Assert.True(JwtTestKeys.Verify(token));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync("/testing/authenticated-sub");

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {string.Join(";", response.Headers.WwwAuthenticate)}");
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("stable-subject", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("wrong-issuer", "dbap-bidding-service")]
    [InlineData("dbap-laravel", "wrong-audience")]
    public async Task InvalidIssuerOrAudienceIsRejected(string issuer, string audience)
    {
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", JwtTestKeys.CreateToken(issuer: issuer, audience: audience));

        var response = await client.GetAsync("/testing/authenticated-sub");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ExpiredTokenIsRejected()
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            JwtTestKeys.CreateToken(expiresAt: DateTimeOffset.UtcNow.AddMinutes(-5)));

        var response = await client.GetAsync("/testing/authenticated-sub");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task MissingTokenIsRejectedByPolicy()
    {
        client.DefaultRequestHeaders.Authorization = null;

        var response = await client.GetAsync("/testing/authenticated-sub");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ValidAuthenticationWithoutPermissionIsForbidden()
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            JwtTestKeys.CreateToken(permissions: ["auction.buy"]));

        var response = await client.GetAsync("/testing/authenticated-sub");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task InvalidSignatureIsRejected()
    {
        using var wrongSigningKey = RSA.Create(2048);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            JwtTestKeys.CreateToken(signingKey: wrongSigningKey));

        var response = await client.GetAsync("/testing/authenticated-sub");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task UnknownSigningKeyIdIsRejected()
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            JwtTestKeys.CreateToken(keyId: "unknown-key"));

        var response = await client.GetAsync("/testing/authenticated-sub");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task BidEndpointRequiresAuthenticatedBidder()
    {
        client.DefaultRequestHeaders.Authorization = null;

        var response = await client.PostAsJsonAsync(
            $"/api/auctions/{TestAuctionData.OpenAuctionId}/bids",
            new { amount = 1250m });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-uuid")]
    public async Task BidEndpointRejectsMissingOrMalformedTenantClaim(string? tenantId)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            JwtTestKeys.CreateToken(tenantId: tenantId));

        var response = await client.PostAsJsonAsync(
            $"/api/auctions/{TestAuctionData.OpenAuctionId}/bids",
            new { amount = 1250m });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task BuyNowEndpointRequiresAuctionBuyPermission()
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            JwtTestKeys.CreateToken(permissions: ["auction.bid"]));

        var response = await client.PostAsJsonAsync(
            $"/api/auctions/{TestAuctionData.OpenAuctionId}/buy-now",
            new { });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("POST", "/api/auctions")]
    [InlineData("PUT", "/api/auctions/00000000-0000-0000-0000-000000000001")]
    [InlineData("DELETE", "/api/auctions/00000000-0000-0000-0000-000000000001")]
    [InlineData("POST", "/api/auctions/00000000-0000-0000-0000-000000000001/cancel")]
    public async Task ManagementEndpointsRequireAuthentication(string method, string path)
    {
        client.DefaultRequestHeaders.Authorization = null;

        using var request = new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = JsonContent.Create(new { version = 1 })
        };

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("POST", "/api/auctions")]
    [InlineData("PUT", "/api/auctions/00000000-0000-0000-0000-000000000001")]
    [InlineData("DELETE", "/api/auctions/00000000-0000-0000-0000-000000000001")]
    [InlineData("POST", "/api/auctions/00000000-0000-0000-0000-000000000001/cancel")]
    public async Task ManagementEndpointsRejectBidderPermission(string method, string path)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            JwtTestKeys.CreateToken(permissions: ["auction.bid", "auction.buy"]));

        using var request = new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = JsonContent.Create(new
            {
                title = "Unauthorized",
                description = "Unauthorized",
                saleMode = "AuctionOnly",
                startingPrice = 100m,
                minimumBidIncrement = 10m,
                startTimeUtc = DateTimeOffset.UtcNow.AddHours(1),
                endTimeUtc = DateTimeOffset.UtcNow.AddHours(2)
            })
        };

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
