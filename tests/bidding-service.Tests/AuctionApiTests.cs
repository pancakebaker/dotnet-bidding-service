using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using bidding_service.Contracts;
using bidding_service.Data;
using bidding_service.Domain;
using bidding_service.Services;
using DistributedBidding.IntegrationContracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace bidding_service.Tests;

[Collection("Bidding service database")]
public sealed class AuctionApiTests : IClassFixture<AuctionApiFactory>, IAsyncLifetime
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-2222-4222-8222-222222222222");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-3333-4333-8333-333333333333");
    private readonly AuctionApiFactory _factory;
    private readonly HttpClient _client;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public AuctionApiTests(AuctionApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                JwtTestKeys.CreateToken(
                    "test-manager",
                    permissions: ["auction.read", "auction.bid", "auction.buy", "auction.manage"]));
    }

    public async Task InitializeAsync() => await _factory.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task GetAuctions_ReturnsAuctionList()
    {
        var auctions = await _client.GetFromJsonAsync<List<AuctionSummaryResponse>>("/api/auctions", JsonOptions);

        Assert.NotNull(auctions);
        Assert.Contains(auctions, a => a.Title == "MacBook Pro");
        Assert.Contains(auctions, a => a.Title == "Camera");
        Assert.Contains(auctions, a => a.Title == "Gaming Console");
    }

    [Fact]
    public async Task GetAuction_ReturnsAuctionDetail()
    {
        var auction = await GetOpenAuctionAsync();

        Assert.Equal("MacBook Pro", auction.Title);
        Assert.Equal("Open", auction.Status);
        Assert.Equal("AuctionOnly", auction.SaleMode);
        Assert.Null(auction.BuyNowPrice);
        Assert.Null(auction.FinalWinnerId);
        Assert.Null(auction.FinalPrice);
        Assert.Equal(1250m, auction.MinimumValidBid);
    }

    [Theory]
    [InlineData("AuctionOnly", 100, 10, false, "Open")]
    [InlineData("BuyNowOnly", 1, 1, true, "Open")]
    [InlineData("AuctionAndBuyNow", 100, 25, true, "Open")]
    public async Task CreateAuction_CreatesConfiguredSaleMode(
        string saleMode,
        decimal startingPrice,
        decimal minimumBidIncrement,
        bool hasBuyNowPrice,
        string expectedStatus)
    {
        decimal? buyNowPrice = hasBuyNowPrice ? 500m : null;
        var response = await CreateAuctionAsync(
            new CreateAuctionRequest(
                "Managed demo auction",
                "Created through the management API.",
                saleMode,
                startingPrice,
                minimumBidIncrement,
                buyNowPrice,
                TestAuctionData.Now.AddHours(-1),
                TestAuctionData.Now.AddHours(2)));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<AuctionDetailResponse>(JsonOptions);
        Assert.NotNull(created);
        Assert.Equal(saleMode, created.SaleMode);
        Assert.Equal(expectedStatus, created.Status);
        Assert.Equal(buyNowPrice, created.BuyNowPrice);
        Assert.Equal(buyNowPrice is not null && saleMode == "BuyNowOnly" ? buyNowPrice : startingPrice, created.StartingPrice);
        Assert.Equal(1, created.Version);

        var persisted = await GetAuctionAsync(created.Id);
        Assert.Equal(created.SaleMode, persisted.SaleMode);
        Assert.Equal(created.BuyNowPrice, persisted.BuyNowPrice);
        Assert.Equal(created.Version, persisted.Version);
    }

    [Fact]
    public async Task CreateAuction_DerivesScheduledStatus()
    {
        var response = await CreateAuctionAsync(new CreateAuctionRequest(
            "Future managed auction",
            "Scheduled through the management API.",
            "AuctionAndBuyNow",
            100m,
            25m,
            500m,
            TestAuctionData.Now.AddHours(1),
            TestAuctionData.Now.AddHours(3)));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<AuctionDetailResponse>(JsonOptions);
        Assert.Equal("Scheduled", created?.Status);
    }

    [Fact]
    public async Task CreateAuctionAssignsTheServerControlledCompatibilityTenant()
    {
        var response = await CreateAuctionAsync(new CreateAuctionRequest(
            "Tenant ownership test",
            "The tenant is assigned by the service compatibility context.",
            "AuctionOnly",
            100m,
            10m,
            null,
            TestAuctionData.Now.AddHours(-1),
            TestAuctionData.Now.AddHours(1)));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BiddingDbContext>();
        var auction = await db.Auctions.SingleAsync(
            item => item.Title == "Tenant ownership test");

        Assert.Equal(TenantDefaults.DemoTenantId, auction.TenantId);
    }

    [Fact]
    public async Task CreateAuctionUsesTheAuthenticatedTenantClaim()
    {
        await EnsureTenantAsync(TenantA);
        UseToken("tenant-a-admin", TenantA, ["auction.manage"]);

        var response = await CreateAuctionAsync(new CreateAuctionRequest(
            "Tenant A auction",
            "Created from Tenant A context.",
            "AuctionOnly",
            100m,
            10m,
            null,
            TestAuctionData.Now.AddHours(1),
            TestAuctionData.Now.AddHours(2)));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<AuctionDetailResponse>(JsonOptions);
        Assert.NotNull(created);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BiddingDbContext>();
        var auction = await db.Auctions.SingleAsync(item => item.Id == created.Id);
        Assert.Equal(TenantA, auction.TenantId);
    }

    [Fact]
    public async Task CreateAuctionRejectsAnUnknownAuthenticatedTenant()
    {
        UseToken("unknown-tenant-admin", Guid.Parse("cccccccc-4444-4444-8444-444444444444"), ["auction.manage"]);

        var response = await CreateAuctionAsync(new CreateAuctionRequest(
            "Unknown tenant auction",
            "Must not be created.",
            "AuctionOnly",
            100m,
            10m,
            null,
            TestAuctionData.Now.AddHours(1),
            TestAuctionData.Now.AddHours(2)));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SuspendedTenantCanReadButCannotCreateOrBid()
    {
        await EnsureTenantStatusAsync(TenantA, TenantStatus.Suspended);
        var auctionId = await AddTenantAuctionAsync(TenantA, SaleMode.AuctionOnly);
        UseToken("tenant-a-reader", TenantA, ["auction.read"]);

        var list = await _client.GetAsync("/api/auctions");
        var detail = await _client.GetAsync($"/api/auctions/{auctionId}");

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);

        var before = await ReadAuctionStateAsync(auctionId);
        var beforeCounts = await ReadSideEffectCountsAsync(auctionId);
        UseToken("tenant-a-bidder", TenantA, ["auction.bid", "auction.manage"]);

        var create = await CreateAuctionAsync(new CreateAuctionRequest(
            "Suspended tenant auction",
            "Must not be created.",
            "AuctionOnly",
            100m,
            10m,
            null,
            TestAuctionData.Now.AddHours(1),
            TestAuctionData.Now.AddHours(2)));
        var bid = await PlaceBidForTenantAsync(auctionId, "tenant-a-bidder", 1250m, TenantA);

        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, bid.StatusCode);
        Assert.Equal(before, await ReadAuctionStateAsync(auctionId));
        Assert.Equal(beforeCounts, await ReadSideEffectCountsAsync(auctionId));
    }

    [Fact]
    public async Task DisabledTenantCannotReadOrMutateButForeignResourcesRemainHidden()
    {
        await EnsureTenantStatusAsync(TenantA, TenantStatus.Disabled);
        var ownAuctionId = await AddTenantAuctionAsync(TenantA, SaleMode.AuctionOnly);
        var foreignAuctionId = await AddTenantAuctionAsync(TenantB, SaleMode.AuctionOnly);
        UseToken("tenant-a-user", TenantA, ["auction.read", "auction.manage"]);

        var list = await _client.GetAsync("/api/auctions");
        var own = await _client.GetAsync($"/api/auctions/{ownAuctionId}");
        var foreign = await _client.GetAsync($"/api/auctions/{foreignAuctionId}");
        var create = await CreateAuctionAsync(new CreateAuctionRequest(
            "Disabled tenant auction",
            "Must not be created.",
            "AuctionOnly",
            100m,
            10m,
            null,
            TestAuctionData.Now.AddHours(1),
            TestAuctionData.Now.AddHours(2)));

        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, own.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
    }

    [Fact]
    public async Task CreateAuctionIgnoresABodyTenantId()
    {
        await EnsureTenantAsync(TenantA);
        UseToken("tenant-a-admin", TenantA, ["auction.manage"]);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auctions")
        {
            Content = JsonContent.Create(new
            {
                title = "Body tenant ignored",
                description = "Ownership must come from the token.",
                saleMode = "AuctionOnly",
                startingPrice = 100m,
                minimumBidIncrement = 10m,
                startTimeUtc = TestAuctionData.Now.AddHours(1),
                endTimeUtc = TestAuctionData.Now.AddHours(2),
                tenantId = TenantB
            }, options: JsonOptions)
        };

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<AuctionDetailResponse>(JsonOptions);
        Assert.NotNull(created);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BiddingDbContext>();
        Assert.Equal(TenantA, (await db.Auctions.SingleAsync(item => item.Id == created.Id)).TenantId);
    }

    [Fact]
    public async Task CrossTenantManagementCommandsReturnNotFoundWithoutMutation()
    {
        await EnsureTenantAsync(TenantA);
        var auctionId = await AddTenantAuctionAsync(TenantB, SaleMode.AuctionOnly, scheduled: true);
        UseToken("tenant-a-admin", TenantA, ["auction.manage"]);

        var before = await ReadAuctionStateAsync(auctionId);
        var update = await UpdateAuctionAsync(auctionId, new UpdateAuctionRequest(
            "Attacker update",
            "Must not apply.",
            "AuctionOnly",
            100m,
            10m,
            null,
            TestAuctionData.Now.AddHours(1),
            TestAuctionData.Now.AddHours(3),
            before.Version));
        var delete = await _client.DeleteAsync($"/api/auctions/{auctionId}");
        var cancel = await CancelAuctionAsync(auctionId, before.Version);

        Assert.Equal(HttpStatusCode.NotFound, update.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, cancel.StatusCode);

        var after = await ReadAuctionStateAsync(auctionId);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal("Tenant B auction", after.Title);
    }

    [Fact]
    public async Task CrossTenantBidReturnsNotFoundWithoutBidOrOutboxSideEffects()
    {
        await EnsureTenantAsync(TenantA);
        var auctionId = await AddTenantAuctionAsync(TenantB, SaleMode.AuctionOnly);
        UseToken("tenant-a-bidder", TenantA, ["auction.bid"]);
        var before = await ReadAuctionStateAsync(auctionId);
        var beforeCounts = await ReadSideEffectCountsAsync(auctionId);

        var response = await PlaceBidAsync(auctionId, "tenant-a-bidder", 1250m);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var after = await ReadAuctionStateAsync(auctionId);
        var afterCounts = await ReadSideEffectCountsAsync(auctionId);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(before.CurrentBidAmount, after.CurrentBidAmount);
        Assert.Equal(beforeCounts, afterCounts);
    }

    [Fact]
    public async Task CrossTenantBuyNowReturnsNotFoundWithoutPurchaseSideEffects()
    {
        await EnsureTenantAsync(TenantA);
        var auctionId = await AddTenantAuctionAsync(TenantB, SaleMode.BuyNowOnly);
        UseToken("tenant-a-bidder", TenantA, ["auction.buy"]);
        var before = await ReadAuctionStateAsync(auctionId);
        var beforeCounts = await ReadSideEffectCountsAsync(auctionId);

        var response = await BuyNowAsync(auctionId, "tenant-a-bidder");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var after = await ReadAuctionStateAsync(auctionId);
        var afterCounts = await ReadSideEffectCountsAsync(auctionId);
        Assert.Equal(before.Version, after.Version);
        Assert.Null(after.FinalWinnerId);
        Assert.Equal(beforeCounts, afterCounts);
    }

    [Fact]
    public async Task TenantStatusAndAuctionOwnershipPersist()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BiddingDbContext>();
        var tenantId = Guid.Parse("bbbbbbbb-2222-4222-8222-222222222222");
        var tenant = Tenant.Create(
            tenantId,
            "Tenant Persistence Test",
            TenantStatus.Suspended,
            TestAuctionData.Now);

        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        var persisted = await db.Tenants.SingleAsync(item => item.Id == tenantId);
        Assert.Equal(TenantStatus.Suspended, persisted.Status);
        Assert.Equal("Tenant Persistence Test", persisted.Name);
    }

    [Fact]
    public async Task ClientApplication_PersistsStatusAndSupportsMultipleApplicationsPerTenant()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BiddingDbContext>();
        var tenantId = Guid.Parse("bbbbbbbb-2222-4222-8222-222222222222");
        db.Tenants.Add(Tenant.Create(tenantId, "Client Registry Tenant", TenantStatus.Active, TestAuctionData.Now));
        db.ClientApplications.AddRange(
            ClientApplication.Create(
                Guid.Parse("cccccccc-3333-4333-8333-333333333333"),
                "customer-a-laravel",
                tenantId,
                "Customer A Laravel",
                ClientApplicationStatus.Active,
                TestAuctionData.Now),
            ClientApplication.Create(
                Guid.Parse("dddddddd-4444-4444-8444-444444444444"),
                "customer-a-wordpress",
                tenantId,
                "Customer A WordPress",
                ClientApplicationStatus.Disabled,
                TestAuctionData.Now),
            ClientApplication.Create(
                Guid.Parse("eeeeeeee-5555-4555-8555-555555555555"),
                "customer-a-reporting",
                tenantId,
                "Customer A Reporting",
                ClientApplicationStatus.Revoked,
                TestAuctionData.Now));

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var persisted = await db.Tenants
            .Include(tenant => tenant.ClientApplications)
            .SingleAsync(tenant => tenant.Id == tenantId);

        Assert.Equal(3, persisted.ClientApplications.Count);
        Assert.Contains(persisted.ClientApplications, item =>
            item.ClientId == "customer-a-laravel" && item.Status == ClientApplicationStatus.Active);
        Assert.Contains(persisted.ClientApplications, item =>
            item.ClientId == "customer-a-wordpress" && item.Status == ClientApplicationStatus.Disabled);
        Assert.Contains(persisted.ClientApplications, item =>
            item.ClientId == "customer-a-reporting" && item.Status == ClientApplicationStatus.Revoked);

        var foreignKey = db.Model
            .FindEntityType(typeof(ClientApplication))!
            .GetForeignKeys()
            .Single(key => key.PrincipalEntityType.ClrType == typeof(Tenant));
        Assert.Equal(DeleteBehavior.Restrict, foreignKey.DeleteBehavior);
    }

    [Fact]
    public async Task ClientApplication_RejectsDuplicateClientIdAndUnknownTenant()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BiddingDbContext>();
        var tenantId = Guid.Parse("bbbbbbbb-2222-4222-8222-222222222222");
        db.Tenants.Add(Tenant.Create(tenantId, "Client Registry Tenant", TenantStatus.Active, TestAuctionData.Now));
        db.ClientApplications.Add(ClientApplication.Create(
            Guid.Parse("cccccccc-3333-4333-8333-333333333333"),
            "duplicate-client",
            tenantId,
            "First application",
            ClientApplicationStatus.Active,
            TestAuctionData.Now));
        await db.SaveChangesAsync();

        db.ClientApplications.Add(ClientApplication.Create(
            Guid.Parse("dddddddd-4444-4444-8444-444444444444"),
            "DUPLICATE-CLIENT",
            tenantId,
            "Duplicate application",
            ClientApplicationStatus.Revoked,
            TestAuctionData.Now));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        db.ClientApplications.Add(ClientApplication.Create(
            Guid.Parse("eeeeeeee-5555-4555-8555-555555555555"),
            "unknown-tenant-client",
            Guid.Parse("ffffffff-6666-4666-8666-666666666666"),
            "Unknown tenant application",
            ClientApplicationStatus.Active,
            TestAuctionData.Now));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public void ClientApplication_ValidatesAndNormalizesClientId()
    {
        var application = ClientApplication.Create(
            Guid.Parse("cccccccc-3333-4333-8333-333333333333"),
            " Customer-A-Laravel ",
            TenantDefaults.DemoTenantId,
            "Customer A Laravel",
            ClientApplicationStatus.Active,
            TestAuctionData.Now);

        Assert.Equal("customer-a-laravel", application.ClientId);
        Assert.Throws<ArgumentException>(() => ClientApplication.NormalizeClientId("contains spaces"));
        Assert.Throws<ArgumentException>(() => ClientApplication.NormalizeClientId("-starts-with-hyphen"));
        Assert.Throws<ArgumentException>(() => ClientApplication.NormalizeClientId(new string('a', 64)));
        Assert.Throws<ArgumentException>(() => ClientApplication.NormalizeClientId(string.Empty));
    }

    [Fact]
    public void ClientCredential_ValidatesKeyIdDatesAndRsaPublicKey()
    {
        var validFrom = TestAuctionData.Now;
        using var rsa = RSA.Create(2048);
        var credential = ClientCredential.Create(
            Guid.Parse("cccccccc-3333-4333-8333-333333333333"),
            TenantDefaults.DemoClientApplicationId,
            " Client-A-Key-01 ",
            rsa.ExportSubjectPublicKeyInfoPem(),
            validFrom,
            validFrom.AddDays(30),
            validFrom);

        Assert.Equal("client-a-key-01", credential.KeyId);
        Assert.StartsWith("-----BEGIN PUBLIC KEY-----", credential.PublicKeyPem);
        Assert.Equal(64, credential.PublicKeyFingerprint.Length);
        Assert.True(credential.IsUsableAt(validFrom));
        Assert.False(credential.IsUsableAt(validFrom.AddDays(30)));
        Assert.Throws<ArgumentException>(() => ClientCredential.NormalizeKeyId("-bad"));
        Assert.Throws<ArgumentException>(() => ClientCredential.NormalizeKeyId("bad-"));
        Assert.Throws<ArgumentException>(() => ClientCredential.NormalizeKeyId(new string('a', 64)));
        Assert.Throws<ArgumentException>(() => ClientCredential.Create(
            Guid.NewGuid(),
            TenantDefaults.DemoClientApplicationId,
            "valid-key",
            rsa.ExportSubjectPublicKeyInfoPem(),
            validFrom,
            validFrom,
            validFrom));
        Assert.Throws<ArgumentException>(() => ClientCredential.Create(
            Guid.NewGuid(),
            TenantDefaults.DemoClientApplicationId,
            "non-utc-key",
            rsa.ExportSubjectPublicKeyInfoPem(),
            new DateTimeOffset(validFrom.DateTime, TimeSpan.FromHours(1)),
            null,
            validFrom));
    }

    [Fact]
    public void ClientCredential_RejectsPrivateAndWeakKeys()
    {
        using var privateKey = RSA.Create(2048);
        Assert.Throws<ArgumentException>(() => ClientCredential.Create(
            Guid.NewGuid(),
            TenantDefaults.DemoClientApplicationId,
            "private-key",
            privateKey.ExportPkcs8PrivateKeyPem(),
            TestAuctionData.Now,
            null,
            TestAuctionData.Now));

        using var weakKey = RSA.Create(1024);
        Assert.Throws<ArgumentException>(() => ClientCredential.Create(
            Guid.NewGuid(),
            TenantDefaults.DemoClientApplicationId,
            "weak-key",
            weakKey.ExportSubjectPublicKeyInfoPem(),
            TestAuctionData.Now,
            null,
            TestAuctionData.Now));
        Assert.Throws<ArgumentException>(() => ClientCredential.Create(
            Guid.NewGuid(),
            TenantDefaults.DemoClientApplicationId,
            "malformed-key",
            "not a PEM key",
            TestAuctionData.Now,
            null,
            TestAuctionData.Now));
    }

    [Fact]
    public void ClientCredential_RejectsNonUtcRevocationWithoutChangingState()
    {
        using var rsa = RSA.Create(2048);
        var credential = ClientCredential.Create(
            Guid.NewGuid(),
            TenantDefaults.DemoClientApplicationId,
            "utc-revocation-key",
            rsa.ExportSubjectPublicKeyInfoPem(),
            TestAuctionData.Now,
            null,
            TestAuctionData.Now);
        var nonUtc = new DateTimeOffset(2026, 9, 13, 12, 0, 0, TimeSpan.FromHours(8));

        Assert.Throws<ArgumentException>(() => credential.Revoke(nonUtc));
        Assert.Equal(ClientCredentialStatus.Active, credential.Status);
        Assert.Null(credential.RevokedAtUtc);
        Assert.True(credential.IsUsableAt(TestAuctionData.Now));

        credential.Revoke(TestAuctionData.Now.AddHours(1));
        var revokedAt = credential.RevokedAtUtc;
        Assert.Throws<ArgumentException>(() => credential.Revoke(nonUtc));
        Assert.Equal(ClientCredentialStatus.Revoked, credential.Status);
        Assert.Equal(revokedAt, credential.RevokedAtUtc);
    }

    [Fact]
    public void ClientCredential_RejectsNonUtcUsabilityInput()
    {
        using var rsa = RSA.Create(2048);
        var credential = ClientCredential.Create(
            Guid.NewGuid(),
            TenantDefaults.DemoClientApplicationId,
            "utc-usability-key",
            rsa.ExportSubjectPublicKeyInfoPem(),
            TestAuctionData.Now,
            null,
            TestAuctionData.Now);

        Assert.Throws<ArgumentException>(() => credential.IsUsableAt(
            new DateTimeOffset(2026, 9, 13, 12, 0, 0, TimeSpan.FromHours(8))));
        Assert.True(credential.IsUsableAt(TestAuctionData.Now));
    }

    [Fact]
    public async Task ClientCredential_PersistsRotationAndRevocationWithoutDeletingHistory()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BiddingDbContext>();
        await EnsureDemoClientApplicationAsync(db);
        using var oldKey = RSA.Create(2048);
        using var newKey = RSA.Create(2048);
        db.ClientCredentials.AddRange(
            ClientCredential.Create(
                Guid.Parse("dddddddd-4444-4444-8444-444444444444"),
                TenantDefaults.DemoClientApplicationId,
                "local-laravel-2026-01",
                oldKey.ExportSubjectPublicKeyInfoPem(),
                TestAuctionData.Now,
                null,
                TestAuctionData.Now),
            ClientCredential.Create(
                Guid.Parse("eeeeeeee-5555-4555-8555-555555555555"),
                TenantDefaults.DemoClientApplicationId,
                "local-laravel-2026-02",
                newKey.ExportSubjectPublicKeyInfoPem(),
                TestAuctionData.Now,
                null,
                TestAuctionData.Now));
        await db.SaveChangesAsync();

        var oldCredential = await db.ClientCredentials.SingleAsync(item => item.KeyId == "local-laravel-2026-01");
        oldCredential.Revoke(TestAuctionData.Now.AddHours(1));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var credentials = await db.ClientCredentials.OrderBy(item => item.KeyId).ToListAsync();
        Assert.Equal(2, credentials.Count);
        Assert.Equal(ClientCredentialStatus.Revoked, credentials[0].Status);
        Assert.NotNull(credentials[0].RevokedAtUtc);
        Assert.False(credentials[0].IsUsableAt(TestAuctionData.Now.AddHours(2)));
        Assert.True(credentials[1].IsUsableAt(TestAuctionData.Now.AddHours(2)));
        Assert.DoesNotContain("PRIVATE KEY", credentials[0].PublicKeyPem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClientCredential_ProvisioningResolvesClientIdAndRevokesIdempotently()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BiddingDbContext>();
        await EnsureDemoClientApplicationAsync(db);
        var provisioning = scope.ServiceProvider.GetRequiredService<IClientCredentialProvisioningService>();
        using var rsa = RSA.Create(2048);

        var credential = await provisioning.ProvisionAsync(
            TenantDefaults.DemoClientId,
            "provisioned-key",
            rsa.ExportSubjectPublicKeyInfoPem(),
            TestAuctionData.Now,
            null);

        Assert.Equal(TenantDefaults.DemoClientApplicationId, credential.ClientApplicationId);
        Assert.True(await provisioning.RevokeAsync("PROVISIONED-KEY"));
        Assert.True(await provisioning.RevokeAsync("provisioned-key"));
        db.ChangeTracker.Clear();
        var stored = await db.ClientCredentials.SingleAsync(item => item.KeyId == "provisioned-key");
        Assert.Equal(ClientCredentialStatus.Revoked, stored.Status);
        Assert.NotNull(stored.RevokedAtUtc);
    }

    [Fact]
    public async Task ClientCredential_ProvisioningAllowsDisabledPreparationButRejectsRevokedApplication()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BiddingDbContext>();
        await EnsureDemoClientApplicationAsync(db);
        var disabled = ClientApplication.Create(
            Guid.Parse("11111111-7777-4777-8777-777777777777"),
            "disabled-client",
            TenantDefaults.DemoTenantId,
            "Disabled Client",
            ClientApplicationStatus.Disabled,
            TestAuctionData.Now);
        var revoked = ClientApplication.Create(
            Guid.Parse("22222222-8888-4888-8888-888888888888"),
            "revoked-client",
            TenantDefaults.DemoTenantId,
            "Revoked Client",
            ClientApplicationStatus.Revoked,
            TestAuctionData.Now);
        db.ClientApplications.AddRange(disabled, revoked);
        await db.SaveChangesAsync();
        var provisioning = scope.ServiceProvider.GetRequiredService<IClientCredentialProvisioningService>();

        using var disabledKey = RSA.Create(2048);
        var prepared = await provisioning.ProvisionAsync(
            "disabled-client",
            "disabled-preparation-key",
            disabledKey.ExportSubjectPublicKeyInfoPem(),
            TestAuctionData.Now,
            null);
        Assert.Equal(disabled.Id, prepared.ClientApplicationId);

        using var revokedKey = RSA.Create(2048);
        await Assert.ThrowsAsync<InvalidOperationException>(() => provisioning.ProvisionAsync(
            "revoked-client",
            "revoked-new-key",
            revokedKey.ExportSubjectPublicKeyInfoPem(),
            TestAuctionData.Now,
            null));
    }

    [Fact]
    public async Task ClientCredential_DatabaseRejectsDuplicateKeyIdAndFingerprint()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BiddingDbContext>();
        await EnsureDemoClientApplicationAsync(db);
        var secondTenant = Guid.Parse("bbbbbbbb-3333-4333-8333-333333333333");
        await EnsureTenantAsync(db, secondTenant);
        var secondApplication = ClientApplication.Create(
            Guid.Parse("ffffffff-6666-4666-8666-666666666666"),
            "second-client",
            secondTenant,
            "Second Client",
            ClientApplicationStatus.Active,
            TestAuctionData.Now);
        db.ClientApplications.Add(secondApplication);
        await db.SaveChangesAsync();

        using var rsa = RSA.Create(2048);
        var publicKey = rsa.ExportSubjectPublicKeyInfoPem();
        db.ClientCredentials.Add(ClientCredential.Create(
            Guid.NewGuid(),
            TenantDefaults.DemoClientApplicationId,
            "duplicate-key",
            publicKey,
            TestAuctionData.Now,
            null,
            TestAuctionData.Now));
        await db.SaveChangesAsync();
        db.ClientCredentials.Add(ClientCredential.Create(
            Guid.NewGuid(),
            secondApplication.Id,
            "duplicate-key",
            publicKey,
            TestAuctionData.Now,
            null,
            TestAuctionData.Now));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        db.ClientCredentials.Add(ClientCredential.Create(
            Guid.NewGuid(),
            secondApplication.Id,
            "different-key",
            publicKey,
            TestAuctionData.Now,
            null,
            TestAuctionData.Now));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task ClientCredential_ForeignKeyAndDeleteAreRestrictive()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BiddingDbContext>();
        await EnsureDemoClientApplicationAsync(db);
        using var rsa = RSA.Create(2048);
        db.ClientCredentials.Add(ClientCredential.Create(
            Guid.NewGuid(),
            TenantDefaults.DemoClientApplicationId,
            "restrictive-delete-key",
            rsa.ExportSubjectPublicKeyInfoPem(),
            TestAuctionData.Now,
            null,
            TestAuctionData.Now));
        await db.SaveChangesAsync();

        db.ChangeTracker.Clear();
        db.ClientApplications.Remove(await db.ClientApplications.SingleAsync(
            item => item.Id == TenantDefaults.DemoClientApplicationId));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        using var invalidKey = RSA.Create(2048);
        db.ClientCredentials.Add(ClientCredential.Create(
            Guid.NewGuid(),
            Guid.Parse("cccccccc-3333-4333-8333-333333333333"),
            "invalid-application-key",
            invalidKey.ExportSubjectPublicKeyInfoPem(),
            TestAuctionData.Now,
            null,
            TestAuctionData.Now));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task DatabaseSeeder_SeedsDeterministicClientApplicationIdempotently()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BiddingDbContext>();
        await db.Database.ExecuteSqlRawAsync("DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public;");
        await db.Database.MigrateAsync();

        await DatabaseSeeder.SeedAsync(db, new FixedTimeProvider(TestAuctionData.Now));
        await DatabaseSeeder.SeedAsync(db, new FixedTimeProvider(TestAuctionData.Now));

        var applications = await db.ClientApplications.ToListAsync();
        Assert.Single(applications);
        Assert.Equal(TenantDefaults.DemoClientApplicationId, applications[0].Id);
        Assert.Equal(TenantDefaults.DemoClientId, applications[0].ClientId);
        Assert.Equal(TenantDefaults.DemoTenantId, applications[0].TenantId);
        Assert.Equal(ClientApplicationStatus.Active, applications[0].Status);
    }

    [Fact]
    public async Task AuctionForeignKeyRejectsAnUnknownTenant()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BiddingDbContext>();
        db.Auctions.Add(new Auction
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.Parse("cccccccc-3333-4333-8333-333333333333"),
            Title = "Invalid tenant auction",
            Description = "The tenant foreign key must reject this row.",
            StartingPrice = 100m,
            SaleMode = SaleMode.AuctionOnly,
            MinimumBidIncrement = 10m,
            StartTimeUtc = TestAuctionData.Now,
            EndTimeUtc = TestAuctionData.Now.AddHours(1),
            Status = AuctionStatus.Scheduled,
            Version = 1,
            CreatedAtUtc = TestAuctionData.Now,
            UpdatedAtUtc = TestAuctionData.Now
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task TenantMigrationBackfillsExistingAuctionWithoutChangingAuctionOrBidIds()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BiddingDbContext>();
        await db.Database.ExecuteSqlRawAsync("DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public;");
        await db.Database.MigrateAsync("20260912052227_AddBuyNowDomainFoundation");

        var auctionId = Guid.Parse("dddddddd-4444-4444-8444-444444444444");
        var bidId = Guid.Parse("eeeeeeee-5555-4555-8555-555555555555");
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO auctions
                (id, title, description, starting_price, sale_mode, buy_now_price,
                 minimum_bid_increment, current_bid_amount, current_bidder_id,
                 final_winner_id, final_price, start_time_utc, end_time_utc,
                 status, version, created_at_utc, updated_at_utc)
            VALUES
                ({auctionId}, 'Legacy auction', 'Pre-MT1 row', 100, 'AuctionOnly', NULL,
                 10, 100, 'legacy-bidder', NULL, NULL,
                 TIMESTAMPTZ '2026-09-05 11:00:00+00', TIMESTAMPTZ '2026-09-05 13:00:00+00',
                 'Open', 2, TIMESTAMPTZ '2026-09-04 12:00:00+00', TIMESTAMPTZ '2026-09-05 12:00:00+00');
            INSERT INTO bids (id, auction_id, bidder_id, amount, created_at_utc)
            VALUES ({bidId}, {auctionId}, 'legacy-bidder', 100,
                    TIMESTAMPTZ '2026-09-05 11:30:00+00');
            """);

        await db.Database.MigrateAsync();

        var migratedAuction = await db.Auctions.SingleAsync(item => item.Id == auctionId);
        var migratedBid = await db.Bids.SingleAsync(item => item.Id == bidId);
        Assert.Equal(TenantDefaults.DemoTenantId, migratedAuction.TenantId);
        Assert.Equal(auctionId, migratedBid.AuctionId);
        Assert.Equal(bidId, migratedBid.Id);
    }

    [Fact]
    public async Task CreateAuction_RejectsInvalidConfigurationAndDoesNotCreateState()
    {
        var requests = new[]
        {
            new CreateAuctionRequest("bad", "bad", "AuctionOnly", 100m, 10m, 500m, TestAuctionData.Now, TestAuctionData.Now.AddHours(1)),
            new CreateAuctionRequest("bad", "bad", "AuctionOnly", 100m, 10m, null, TestAuctionData.Now, TestAuctionData.Now),
            new CreateAuctionRequest("bad", "bad", "BuyNowOnly", 100m, 10m, null, TestAuctionData.Now, TestAuctionData.Now.AddHours(1)),
            new CreateAuctionRequest("bad", "bad", "AuctionAndBuyNow", 500m, 10m, 500m, TestAuctionData.Now, TestAuctionData.Now.AddHours(1)),
            new CreateAuctionRequest("bad", "bad", "AuctionOnly", 100.001m, 10m, null, TestAuctionData.Now, TestAuctionData.Now.AddHours(1))
        };

        foreach (var request in requests)
        {
            var response = await CreateAuctionAsync(request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var error = await response.Content.ReadFromJsonAsync<ApiErrorResponse>(JsonOptions);
            Assert.NotNull(error?.Code);
        }
    }

    [Fact]
    public async Task CreateAuction_IgnoresCallerControlledAggregateState()
    {
        var payload = new
        {
            title = "Overposting test",
            description = "Server-owned fields must be ignored.",
            saleMode = "AuctionOnly",
            startingPrice = 100m,
            minimumBidIncrement = 10m,
            buyNowPrice = (decimal?)null,
            startTimeUtc = TestAuctionData.Now.AddHours(-1),
            endTimeUtc = TestAuctionData.Now.AddHours(1),
            status = "Closed",
            version = 99,
            currentBidAmount = 999m,
            currentBidderId = "attacker",
            finalWinnerId = "attacker",
            finalPrice = 999m,
            createdAtUtc = TestAuctionData.Now.AddYears(-1),
            updatedAtUtc = TestAuctionData.Now.AddYears(-1)
        };

        var response = await _client.PostAsJsonAsync("/api/auctions", payload, JsonOptions);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<AuctionDetailResponse>(JsonOptions);
        Assert.NotNull(created);
        Assert.Equal("Open", created.Status);
        Assert.Equal(1, created.Version);
        Assert.Null(created.CurrentBidAmount);
        Assert.Null(created.CurrentBidderId);
        Assert.Null(created.FinalWinnerId);
        Assert.Null(created.FinalPrice);
    }

    [Fact]
    public async Task ScheduledAuction_CanBeUpdatedWithExpectedVersion()
    {
        var createdResponse = await CreateAuctionAsync(new CreateAuctionRequest(
            "Editable auction",
            "Before-start configuration.",
            "AuctionOnly",
            100m,
            10m,
            null,
            TestAuctionData.Now.AddHours(1),
            TestAuctionData.Now.AddHours(3)));
        var created = await createdResponse.Content.ReadFromJsonAsync<AuctionDetailResponse>(JsonOptions);

        var update = await UpdateAuctionAsync(created!.Id, new UpdateAuctionRequest(
            "Edited auction",
            "Updated before start.",
            "AuctionAndBuyNow",
            100m,
            25m,
            500m,
            TestAuctionData.Now.AddHours(2),
            TestAuctionData.Now.AddHours(4),
            created.Version));

        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var updated = await update.Content.ReadFromJsonAsync<AuctionDetailResponse>(JsonOptions);
        Assert.Equal("AuctionAndBuyNow", updated?.SaleMode);
        Assert.Equal(500m, updated?.BuyNowPrice);
        Assert.Equal(2, updated?.Version);
        Assert.Equal("Edited auction", (await GetAuctionAsync(created.Id)).Title);

        var stale = await UpdateAuctionAsync(created.Id, new UpdateAuctionRequest(
            "Stale edit", "Should be rejected.", "AuctionOnly", 200m, 10m, null,
            TestAuctionData.Now.AddHours(2), TestAuctionData.Now.AddHours(4), created.Version));
        await AssertManagementRejectedAsync(stale, "auction_concurrency_conflict");
        Assert.Equal(2, (await GetAuctionAsync(created.Id)).Version);
    }

    [Fact]
    public async Task OpenAndClosedAuctionsAreNotEditable()
    {
        var open = await UpdateAuctionAsync(TestAuctionData.OpenAuctionId, new UpdateAuctionRequest(
            "No edit", "No edit", "AuctionOnly", 100m, 10m, null,
            TestAuctionData.Now.AddHours(1), TestAuctionData.Now.AddHours(2), 3));
        var closed = await UpdateAuctionAsync(TestAuctionData.ClosedAuctionId, new UpdateAuctionRequest(
            "No edit", "No edit", "AuctionOnly", 100m, 10m, null,
            TestAuctionData.Now.AddHours(1), TestAuctionData.Now.AddHours(2), 2));

        await AssertManagementRejectedAsync(open, "auction_not_editable");
        await AssertManagementRejectedAsync(closed, "auction_not_editable");
    }

    [Fact]
    public async Task DeleteOnlyRemovesUntouchedFutureScheduledAuction()
    {
        var created = await CreateAuctionAsync(new CreateAuctionRequest(
            "Disposable auction", "Safe to delete.", "AuctionOnly", 100m, 10m, null,
            TestAuctionData.Now.AddHours(1), TestAuctionData.Now.AddHours(3)));
        var auction = await created.Content.ReadFromJsonAsync<AuctionDetailResponse>(JsonOptions);

        var deleted = await _client.DeleteAsync($"/api/auctions/{auction!.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/auctions/{auction.Id}")).StatusCode);

        await AssertManagementRejectedAsync(
            await _client.DeleteAsync($"/api/auctions/{TestAuctionData.OpenAuctionId}"),
            "auction_not_deletable");
        await AssertManagementRejectedAsync(
            await _client.DeleteAsync($"/api/auctions/{TestAuctionData.ClosedAuctionId}"),
            "auction_not_deletable");
    }

    [Fact]
    public async Task CancelScheduledAuctionCommitsOneVersionAndEvent()
    {
        var createdResponse = await CreateAuctionAsync(new CreateAuctionRequest(
            "Cancellable scheduled", "Retained after cancellation.", "AuctionOnly", 100m, 10m, null,
            TestAuctionData.Now.AddHours(1), TestAuctionData.Now.AddHours(3)));
        var created = await createdResponse.Content.ReadFromJsonAsync<AuctionDetailResponse>(JsonOptions);

        var response = await CancelAuctionAsync(created!.Id, created.Version);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cancelled = await response.Content.ReadFromJsonAsync<AuctionDetailResponse>(JsonOptions);
        Assert.Equal("Cancelled", cancelled?.Status);
        Assert.Equal(2, cancelled?.Version);
        Assert.Null(cancelled?.FinalWinnerId);
        Assert.Null(cancelled?.FinalPrice);
        var messages = await GetOutboxMessagesAsync(created.Id);
        Assert.Single(messages);
        Assert.Equal(IntegrationEventTypes.AuctionCancelled, messages[0].EventType);
    }

    [Fact]
    public async Task CancelOpenBidBearingAuctionPreservesBidsAndRejectsLaterCommands()
    {
        var auctionId = await AddBuyNowAuctionAsync(SaleMode.AuctionAndBuyNow, startingPrice: 100m, minimumBidIncrement: 10m, buyNowPrice: 500m);
        var bid = await PlaceBidAsync(auctionId, "bidder-1", 100m);
        Assert.Equal(HttpStatusCode.Created, bid.StatusCode);
        var before = await GetAuctionAsync(auctionId);

        var response = await CancelAuctionAsync(auctionId, before.Version);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cancelled = await GetAuctionAsync(auctionId);
        Assert.Equal("Cancelled", cancelled.Status);
        Assert.Equal(before.CurrentBidAmount, cancelled.CurrentBidAmount);
        Assert.Equal(before.CurrentBidderId, cancelled.CurrentBidderId);
        Assert.Null(cancelled.FinalWinnerId);
        Assert.Null(cancelled.FinalPrice);
        Assert.Single(await GetBidsAsync(auctionId));
        await AssertBuyNowRejectedAsync(await BuyNowAsync(auctionId, "buyer-1"), "auction_not_open");
        await AssertBidRejectedAsync(await PlaceBidAsync(auctionId, "bidder-2", 120m), "auction_not_open");
    }

    [Fact]
    public async Task CancelClosedAuctionAndStaleCancellationAreRejected()
    {
        var stale = await AddBuyNowAuctionAsync(SaleMode.AuctionOnly, buyNowPrice: null);
        var staleState = await GetAuctionAsync(stale);
        await AssertManagementRejectedAsync(await CancelAuctionAsync(stale, 0), "auction_concurrency_conflict");
        Assert.Equal(1, (await GetAuctionAsync(stale)).Version);
        var accepted = await CancelAuctionAsync(stale, staleState.Version);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        await AssertManagementRejectedAsync(await CancelAuctionAsync(stale, staleState.Version), "auction_already_cancelled");
        await AssertManagementRejectedAsync(await CancelAuctionAsync(TestAuctionData.ClosedAuctionId, 2), "auction_not_cancellable");
    }

    [Fact]
    public async Task CreatedBuyNowModesRemainCompatibleWithExistingCommands()
    {
        var buyNowOnly = await CreateAuctionAsync(new CreateAuctionRequest(
            "Created Buy Now", "Purchase this auction.", "BuyNowOnly", 1m, 1m, 500m,
            TestAuctionData.Now.AddHours(-1), TestAuctionData.Now.AddHours(1)));
        var buyNowAuction = await buyNowOnly.Content.ReadFromJsonAsync<AuctionDetailResponse>(JsonOptions);
        var purchase = await BuyNowAsync(buyNowAuction!.Id, "buyer-from-api");
        Assert.Equal(HttpStatusCode.Created, purchase.StatusCode);

        var combined = await CreateAuctionAsync(new CreateAuctionRequest(
            "Created combined", "Bid before Buy Now.", "AuctionAndBuyNow", 100m, 25m, 500m,
            TestAuctionData.Now.AddHours(-1), TestAuctionData.Now.AddHours(1)));
        var combinedAuction = await combined.Content.ReadFromJsonAsync<AuctionDetailResponse>(JsonOptions);
        Assert.Equal(HttpStatusCode.Created, (await PlaceBidAsync(combinedAuction!.Id, "bidder-from-api", 125m)).StatusCode);
    }

    [Fact]
    public async Task DatabaseSeeder_CoversAllSaleModes()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BiddingDbContext>();
        await db.Database.ExecuteSqlRawAsync("DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public;");
        await db.Database.MigrateAsync();
        await DatabaseSeeder.SeedAsync(db, new FixedTimeProvider(TestAuctionData.Now));

        var modes = await db.Auctions.Select(auction => auction.SaleMode).Distinct().ToListAsync();
        Assert.Contains(SaleMode.AuctionOnly, modes);
        Assert.Contains(SaleMode.BuyNowOnly, modes);
        Assert.Contains(SaleMode.AuctionAndBuyNow, modes);
        Assert.All(await db.Auctions.Where(a => a.SaleMode == SaleMode.BuyNowOnly).ToListAsync(), auction =>
        {
            Assert.Equal(auction.BuyNowPrice, auction.StartingPrice);
            Assert.True(auction.BuyNowPrice > 0);
        });
    }

    [Fact]
    public async Task BuyNowFields_PersistWithExistingMoneyPrecision()
    {
        var auctionId = Guid.NewGuid();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BiddingDbContext>();
        db.Auctions.Add(new Auction
        {
            Id = auctionId,
            TenantId = TenantDefaults.DemoTenantId,
            Title = "Buy Now test auction",
            Description = "Buy Now persistence foundation test.",
            StartingPrice = 1250m,
            SaleMode = SaleMode.BuyNowOnly,
            BuyNowPrice = 1250.67m,
            MinimumBidIncrement = 50m,
            StartTimeUtc = TestAuctionData.Now.AddHours(-1),
            EndTimeUtc = TestAuctionData.Now.AddHours(1),
            Status = AuctionStatus.Open,
            Version = 1,
            CreatedAtUtc = TestAuctionData.Now,
            UpdatedAtUtc = TestAuctionData.Now
        });

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var persisted = await db.Auctions.SingleAsync(auction => auction.Id == auctionId);
        Assert.Equal(SaleMode.BuyNowOnly, persisted.SaleMode);
        Assert.Equal(1250.67m, persisted.BuyNowPrice);
        Assert.Null(persisted.CurrentBidAmount);
        Assert.Null(persisted.CurrentBidderId);
        Assert.Null(persisted.FinalWinnerId);
        Assert.Null(persisted.FinalPrice);
    }

    [Fact]
    public async Task AuctionAndBuyNowWithNonIncreasingStartingPrice_IsRejected()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BiddingDbContext>();
        db.Auctions.Add(new Auction
        {
            Id = Guid.NewGuid(),
            TenantId = TenantDefaults.DemoTenantId,
            Title = "Invalid Buy Now auction",
            Description = "Invalid sale-mode combination.",
            StartingPrice = 1000m,
            SaleMode = SaleMode.AuctionAndBuyNow,
            BuyNowPrice = 1000m,
            MinimumBidIncrement = 50m,
            StartTimeUtc = TestAuctionData.Now,
            EndTimeUtc = TestAuctionData.Now.AddHours(1),
            Status = AuctionStatus.Scheduled,
            Version = 1,
            CreatedAtUtc = TestAuctionData.Now,
            UpdatedAtUtc = TestAuctionData.Now
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Theory]
    [InlineData(SaleMode.BuyNowOnly)]
    [InlineData(SaleMode.AuctionAndBuyNow)]
    public async Task BuyNow_WhenEligible_CommitsTerminalStateAndSiblingEvents(SaleMode saleMode)
    {
        var auctionId = await AddBuyNowAuctionAsync(saleMode);

        var response = await BuyNowAsync(auctionId, "buyer-123", "buy-now-correlation");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var purchase = await response.Content.ReadFromJsonAsync<BuyNowResponse>(JsonOptions);
        Assert.NotNull(purchase);
        Assert.Equal(auctionId, purchase.AuctionId);
        Assert.Equal("buyer-123", purchase.BidderId);
        Assert.Equal(1500m, purchase.FinalPrice);
        Assert.Equal(2, purchase.AuctionVersion);
        Assert.Equal("buy-now-correlation", purchase.CorrelationId);
        Assert.Equal("buy-now-correlation", response.Headers.GetValues("X-Correlation-ID").Single());

        var auction = await GetAuctionAsync(auctionId);
        Assert.Equal("Closed", auction.Status);
        Assert.Equal("buyer-123", auction.FinalWinnerId);
        Assert.Equal(1500m, auction.FinalPrice);
        Assert.Equal(2, auction.Version);
        Assert.Null(auction.CurrentBidAmount);
        Assert.Null(auction.CurrentBidderId);
        Assert.Empty(await GetBidsAsync(auctionId));

        var messages = await GetOutboxMessagesAsync(auctionId);
        Assert.Equal(2, messages.Count);
        Assert.Equal(IntegrationEventTypes.AuctionPurchased, messages[0].EventType);
        Assert.Equal(IntegrationEventTypes.AuctionClosed, messages[1].EventType);
        Assert.All(messages, message =>
        {
            Assert.Equal(2, message.AggregateVersion);
            Assert.Equal("buy-now-correlation", message.CorrelationId);
        });
        Assert.NotEqual(messages[0].Id, messages[1].Id);
        Assert.DoesNotContain(messages, message => message.EventType == IntegrationEventTypes.WinnerSelected);

        var purchasedPayload = JsonSerializer.Deserialize<AuctionPurchasedPayload>(messages[0].Payload, JsonOptions);
        Assert.NotNull(purchasedPayload);
        Assert.Equal(auctionId, purchasedPayload.AuctionId);
        Assert.Equal("buyer-123", purchasedPayload.BidderId);
        Assert.Equal(1500m, purchasedPayload.FinalPrice);
        Assert.Equal(2, purchasedPayload.AuctionVersion);
    }

    [Fact]
    public async Task BuyNow_OnAuctionOnly_IsRejectedWithoutChangingState()
    {
        var response = await BuyNowAsync(TestAuctionData.OpenAuctionId, "buyer-123");

        await AssertBuyNowRejectedAsync(response, "buy_now_not_available");
        var auction = await GetAuctionAsync(TestAuctionData.OpenAuctionId);
        Assert.Equal("Open", auction.Status);
        Assert.Equal(3, auction.Version);
        Assert.Null(auction.FinalWinnerId);
        Assert.Null(auction.FinalPrice);
        Assert.Empty(await GetOutboxMessagesAsync(TestAuctionData.OpenAuctionId));
    }

    [Fact]
    public async Task BuyNow_InvalidOrUnavailableRequestsUseSpecificErrors()
    {
        var invalidBidder = await BuyNowAsync(TestAuctionData.OpenAuctionId, " ");
        var missingAuction = await BuyNowAsync(Guid.NewGuid(), "buyer-123");
        var scheduled = await BuyNowAsync(TestAuctionData.ScheduledAuctionId, "buyer-123");
        var beforeStart = await AddBuyNowAuctionAsync(
            SaleMode.BuyNowOnly,
            startTimeUtc: TestAuctionData.Now.AddMinutes(1));
        var ended = await AddBuyNowAuctionAsync(
            SaleMode.BuyNowOnly,
            endTimeUtc: TestAuctionData.Now);
        var closed = await AddBuyNowAuctionAsync(
            SaleMode.BuyNowOnly,
            status: AuctionStatus.Closed);

        Assert.Equal(HttpStatusCode.BadRequest, invalidBidder.StatusCode);
        Assert.Equal("invalid_bidder", (await invalidBidder.Content.ReadFromJsonAsync<ApiErrorResponse>(JsonOptions))?.Code);
        Assert.Equal(HttpStatusCode.NotFound, missingAuction.StatusCode);
        Assert.Equal("auction_not_found", (await missingAuction.Content.ReadFromJsonAsync<ApiErrorResponse>(JsonOptions))?.Code);
        await AssertBuyNowRejectedAsync(scheduled, "auction_not_open");
        await AssertBuyNowRejectedAsync(await BuyNowAsync(beforeStart, "buyer-123"), "auction_not_started");
        await AssertBuyNowRejectedAsync(await BuyNowAsync(ended, "buyer-123"), "auction_ended");
        await AssertBuyNowRejectedAsync(await BuyNowAsync(closed, "buyer-123"), "auction_not_open");
    }

    [Fact]
    public async Task BuyNowOnly_RejectsOrdinaryBids()
    {
        var auctionId = await AddBuyNowAuctionAsync(SaleMode.BuyNowOnly);

        var response = await PlaceBidAsync(auctionId, "bidder-123", 1100m);

        await AssertBidRejectedAsync(response, "bidding_not_available");
        var auction = await GetAuctionAsync(auctionId);
        Assert.Equal(1, auction.Version);
        Assert.Empty(await GetBidsAsync(auctionId));
        Assert.Empty(await GetOutboxMessagesAsync(auctionId));
    }

    [Fact]
    public async Task AuctionAndBuyNow_AcceptsBelowPrice()
    {
        var auctionId = await AddBuyNowAuctionAsync(SaleMode.AuctionAndBuyNow);

        var accepted = await PlaceBidAsync(auctionId, "bidder-123", 1250m);

        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);

        var auction = await GetAuctionAsync(auctionId);
        Assert.Equal(1250m, auction.CurrentBidAmount);
        Assert.Equal("bidder-123", auction.CurrentBidderId);
        Assert.Equal(2, auction.Version);
        Assert.Single(await GetBidsAsync(auctionId));
        Assert.Single(await GetOutboxMessagesAsync(auctionId));
    }

    [Theory]
    [InlineData(1500)]
    [InlineData(1501)]
    public async Task AuctionAndBuyNow_BidAtOrAbovePrice_NormalizesAndCloses(decimal submittedAmount)
    {
        var auctionId = await AddBuyNowAuctionAsync(SaleMode.AuctionAndBuyNow);

        var response = await PlaceBidAsync(auctionId, "bidder-123", submittedAmount);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var bidResponse = await response.Content.ReadFromJsonAsync<PlaceBidResponse>(JsonOptions);
        Assert.NotNull(bidResponse);
        Assert.Equal(1500m, bidResponse.Amount);
        Assert.Equal(1500m, bidResponse.CurrentBidAmount);

        var auction = await GetAuctionAsync(auctionId);
        Assert.Equal("Closed", auction.Status);
        Assert.Equal(1500m, auction.CurrentBidAmount);
        Assert.Equal("bidder-123", auction.CurrentBidderId);
        Assert.Equal("bidder-123", auction.FinalWinnerId);
        Assert.Equal(1500m, auction.FinalPrice);
        Assert.Equal(2, auction.Version);

        var bids = await GetBidsAsync(auctionId);
        var bid = Assert.Single(bids);
        Assert.Equal(1500m, bid.Amount);

        var messages = await GetOutboxMessagesAsync(auctionId);
        Assert.Equal(3, messages.Count);
        Assert.Single(messages, message => message.EventType == IntegrationEventTypes.BidAccepted);
        Assert.Single(messages, message => message.EventType == IntegrationEventTypes.AuctionPurchased);
        Assert.Single(messages, message => message.EventType == IntegrationEventTypes.AuctionClosed);
        Assert.All(messages, message => Assert.Equal(2, message.AggregateVersion));
    }

    [Fact]
    public async Task AuctionAndBuyNow_WhenNextMinimumReachesPrice_RemainsValidWithoutLegalNextBid()
    {
        var auctionId = await AddBuyNowAuctionAsync(
            SaleMode.AuctionAndBuyNow,
            startingPrice: 100m,
            minimumBidIncrement: 100m,
            buyNowPrice: 500m,
            currentBidAmount: 450m,
            currentBidderId: "existing-bidder",
            version: 2);

        var response = await PlaceBidAsync(auctionId, "bidder-123", 550m);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var auction = await GetAuctionAsync(auctionId);
        Assert.Equal("Closed", auction.Status);
        Assert.Equal(3, auction.Version);
        Assert.Equal(500m, auction.CurrentBidAmount);
        Assert.Equal("bidder-123", auction.FinalWinnerId);
        Assert.Equal(500m, auction.FinalPrice);
        Assert.Single(await GetBidsAsync(auctionId));
        Assert.Equal(3, (await GetOutboxMessagesAsync(auctionId)).Count);
    }

    [Fact]
    public async Task ConcurrentBuyNowThresholdBidsCommitOneNormalizedPurchase()
    {
        var auctionId = await AddBuyNowAuctionAsync(SaleMode.AuctionAndBuyNow);

        var responses = await Task.WhenAll(
            PlaceBidAsync(auctionId, "bidder-1", 1500m),
            PlaceBidAsync(auctionId, "bidder-2", 1600m));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
        var rejected = Assert.Single(responses, response => response.StatusCode != HttpStatusCode.Created);
        var rejectedError = await rejected.Content.ReadFromJsonAsync<ApiErrorResponse>(JsonOptions);
        Assert.True(rejectedError?.Code is "auction_not_open" or "auction_concurrency_conflict");

        var auction = await GetAuctionAsync(auctionId);
        Assert.Equal("Closed", auction.Status);
        Assert.Equal(2, auction.Version);
        Assert.Equal(1500m, auction.FinalPrice);
        Assert.Single(await GetBidsAsync(auctionId));

        var messages = await GetOutboxMessagesAsync(auctionId);
        Assert.Equal(3, messages.Count);
        Assert.Single(messages, message => message.EventType == IntegrationEventTypes.BidAccepted);
        Assert.Single(messages, message => message.EventType == IntegrationEventTypes.AuctionPurchased);
        Assert.Single(messages, message => message.EventType == IntegrationEventTypes.AuctionClosed);
    }

    [Fact]
    public async Task ConcurrentBuyNowRequestsCommitOnePurchaseAndReturnSpecificLoserError()
    {
        var auctionId = await AddBuyNowAuctionAsync(SaleMode.BuyNowOnly);

        var responses = await Task.WhenAll(
            BuyNowAsync(auctionId, "buyer-1"),
            BuyNowAsync(auctionId, "buyer-2"));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
        var rejected = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        var rejectedError = await rejected.Content.ReadFromJsonAsync<ApiErrorResponse>(JsonOptions);
        Assert.True(rejectedError?.Code is "auction_not_open" or "buy_now_not_available");

        var auction = await GetAuctionAsync(auctionId);
        Assert.Equal("Closed", auction.Status);
        Assert.Equal(2, auction.Version);
        Assert.NotNull(auction.FinalWinnerId);
        Assert.Equal(1500m, auction.FinalPrice);
        var messages = await GetOutboxMessagesAsync(auctionId);
        Assert.Equal(2, messages.Count);
        Assert.Single(messages, message => message.EventType == IntegrationEventTypes.AuctionPurchased);
    }

    [Fact]
    public async Task DuplicateBuyNowSubmissionDoesNotCreateDuplicatePurchase()
    {
        var auctionId = await AddBuyNowAuctionAsync(SaleMode.BuyNowOnly);

        var first = await BuyNowAsync(auctionId, "buyer-1");
        var second = await BuyNowAsync(auctionId, "buyer-1");

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        await AssertBuyNowRejectedAsync(second, "auction_not_open");

        var auction = await GetAuctionAsync(auctionId);
        Assert.Equal("Closed", auction.Status);
        Assert.Equal(2, auction.Version);
        Assert.Equal("buyer-1", auction.FinalWinnerId);
        Assert.Equal(1500m, auction.FinalPrice);

        var messages = await GetOutboxMessagesAsync(auctionId);
        Assert.Equal(2, messages.Count);
        Assert.Single(messages, message => message.EventType == IntegrationEventTypes.AuctionPurchased);
        Assert.Single(messages, message => message.EventType == IntegrationEventTypes.AuctionClosed);
    }

    [Fact]
    public async Task BuyNowAndBidRejectAtExactEndTimeBoundary()
    {
        var auctionId = await AddBuyNowAuctionAsync(
            SaleMode.AuctionAndBuyNow,
            endTimeUtc: TestAuctionData.Now);

        await AssertBuyNowRejectedAsync(await BuyNowAsync(auctionId, "buyer-1"), "auction_ended");
        await AssertBidRejectedAsync(
            await PlaceBidAsync(auctionId, "bidder-1", 1250m),
            "auction_ended");

        var auction = await GetAuctionAsync(auctionId);
        Assert.Equal("Open", auction.Status);
        Assert.Equal(1, auction.Version);
        Assert.Null(auction.FinalWinnerId);
        Assert.Null(auction.FinalPrice);
        Assert.Empty(await GetBidsAsync(auctionId));
        Assert.Empty(await GetOutboxMessagesAsync(auctionId));
    }

    [Fact]
    public async Task BuyNowAndOrdinaryBidRaceLeavesOnePurchaseAndConsistentTerminalState()
    {
        var auctionId = await AddBuyNowAuctionAsync(SaleMode.AuctionAndBuyNow);

        var responses = await Task.WhenAll(
            BuyNowAsync(auctionId, "buyer-1"),
            PlaceBidAsync(auctionId, "bidder-1", 1250m));

        var acceptedBid = responses[1].StatusCode == HttpStatusCode.Created ? 1 : 0;
        var auction = await GetAuctionAsync(auctionId);
        var bids = await GetBidsAsync(auctionId);
        var messages = await GetOutboxMessagesAsync(auctionId);

        Assert.Equal("Closed", auction.Status);
        Assert.Equal(2 + acceptedBid, auction.Version);
        Assert.NotNull(auction.FinalWinnerId);
        Assert.Equal(1500m, auction.FinalPrice);
        Assert.Equal(acceptedBid, bids.Count);
        Assert.Equal(acceptedBid + 2, messages.Count);
        Assert.Single(messages, message => message.EventType == IntegrationEventTypes.AuctionPurchased);
        Assert.Single(messages, message => message.EventType == IntegrationEventTypes.AuctionClosed);
        Assert.DoesNotContain(messages, message => message.EventType == IntegrationEventTypes.WinnerSelected);
    }

    [Fact]
    public async Task GetAuction_WhenMissing_ReturnsNotFound()
    {
        var response = await _client.GetAsync($"/api/auctions/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiErrorResponse>(JsonOptions);
        Assert.Equal("auction_not_found", error?.Code);
    }

    [Fact]
    public async Task PlaceBid_WhenValid_AcceptsBid()
    {
        var response = await PlaceBidAsync("dana", 1250m);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var bid = await response.Content.ReadFromJsonAsync<PlaceBidResponse>(JsonOptions);
        Assert.NotNull(bid);
        Assert.Equal("dana", bid.BidderId);
        Assert.Equal(1250m, bid.Amount);
        Assert.Equal(1300m, bid.NextMinimumBid);
    }

    [Fact]
    public async Task PlaceBid_WhenBelowMinimum_RejectsBid()
    {
        var response = await PlaceBidAsync("dana", 1249.99m);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiErrorResponse>(JsonOptions);
        Assert.Equal("bid_below_minimum", error?.Code);
    }

    [Fact]
    public async Task PlaceBid_OnScheduledAuction_RejectsBid()
    {
        var response = await _client.PostAsJsonAsync($"/api/auctions/{TestAuctionData.ScheduledAuctionId}/bids", new PlaceBidRequest(500m, "dana"), JsonOptions);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiErrorResponse>(JsonOptions);
        Assert.Equal("auction_not_open", error?.Code);
    }

    [Fact]
    public async Task PlaceBid_OnClosedAuction_RejectsBid()
    {
        var response = await _client.PostAsJsonAsync($"/api/auctions/{TestAuctionData.ClosedAuctionId}/bids", new PlaceBidRequest(500m, "dana"), JsonOptions);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiErrorResponse>(JsonOptions);
        Assert.Equal("auction_not_open", error?.Code);
    }

    [Fact]
    public async Task PlaceBid_AfterEndTime_RejectsBid()
    {
        var response = await _client.PostAsJsonAsync($"/api/auctions/{TestAuctionData.EndedOpenAuctionId}/bids", new PlaceBidRequest(500m, "dana"), JsonOptions);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiErrorResponse>(JsonOptions);
        Assert.Equal("auction_ended", error?.Code);
    }

    [Fact]
    public async Task PlaceBid_BeforeStartTime_RejectsBid()
    {
        var response = await _client.PostAsJsonAsync($"/api/auctions/{TestAuctionData.FutureOpenAuctionId}/bids", new PlaceBidRequest(500m, "dana"), JsonOptions);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiErrorResponse>(JsonOptions);
        Assert.Equal("auction_not_started", error?.Code);
    }

    [Fact]
    public async Task PlaceBid_WhenSuccessful_UpdatesAuctionState()
    {
        await PlaceBidAsync("dana", 1250m);

        var auction = await GetOpenAuctionAsync();
        Assert.Equal(1250m, auction.CurrentBidAmount);
        Assert.Equal("dana", auction.CurrentBidderId);
        Assert.Equal(4, auction.Version);
    }

    [Fact]
    public async Task PlaceBid_WhenSuccessful_CreatesBidHistoryRecord()
    {
        await PlaceBidAsync("dana", 1250m);

        var bids = await GetOpenAuctionBidsAsync();
        Assert.Equal("dana", bids[0].BidderId);
        Assert.Equal(1250m, bids[0].Amount);
        Assert.Equal(3, bids.Count);
    }

    [Fact]
    public async Task PlaceBid_WhenAccepted_CreatesBidAcceptedOutboxMessage()
    {
        var response = await PlaceBidAsync("dana", 1250m);
        var acceptedBid = await response.Content.ReadFromJsonAsync<PlaceBidResponse>(JsonOptions);
        var messages = await GetOutboxMessagesAsync();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(acceptedBid);
        var message = Assert.Single(messages);
        Assert.Equal(IntegrationEventTypes.BidAccepted, message.EventType);
        Assert.Equal(AggregateTypes.Auction, message.AggregateType);
        Assert.Equal(TestAuctionData.OpenAuctionId, message.AggregateId);
        Assert.Equal(acceptedBid.AuctionVersion, message.AggregateVersion);
        Assert.Null(message.PublishedAtUtc);
        Assert.Equal(0, message.PublishAttempts);

        var payload = JsonSerializer.Deserialize<BidAcceptedPayload>(message.Payload, JsonOptions);
        Assert.NotNull(payload);
        Assert.Equal(acceptedBid.BidId, payload.BidId);
        Assert.Equal(TestAuctionData.OpenAuctionId, payload.AuctionId);
        Assert.Equal("dana", payload.BidderId);
        Assert.Equal(1250m, payload.Amount);
        Assert.Equal(acceptedBid.AuctionVersion, payload.AuctionVersion);
    }

    [Fact]
    public async Task PlaceBid_WithCorrelationHeader_PreservesCorrelationIdInResponseHeaderAndOutbox()
    {
        const string correlationId = "phase3-correlation-123";
        var response = await PlaceBidAsync("dana", 1250m, correlationId);
        var acceptedBid = await response.Content.ReadFromJsonAsync<PlaceBidResponse>(JsonOptions);
        var message = Assert.Single(await GetOutboxMessagesAsync());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(acceptedBid);
        Assert.Equal(correlationId, acceptedBid.CorrelationId);
        Assert.True(response.Headers.TryGetValues("X-Correlation-ID", out var values));
        Assert.Equal(correlationId, Assert.Single(values));
        Assert.Equal(correlationId, message.CorrelationId);
    }

    [Fact]
    public async Task PlaceBid_WithoutCorrelationHeader_GeneratesCorrelationId()
    {
        var response = await PlaceBidAsync("dana", 1250m);
        var acceptedBid = await response.Content.ReadFromJsonAsync<PlaceBidResponse>(JsonOptions);
        var message = Assert.Single(await GetOutboxMessagesAsync());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(acceptedBid);
        Assert.False(string.IsNullOrWhiteSpace(acceptedBid.CorrelationId));
        Assert.Equal(acceptedBid.CorrelationId, message.CorrelationId);
        Assert.True(Guid.TryParse(acceptedBid.CorrelationId, out _));
    }

    [Fact]
    public async Task PlaceBid_WhenBelowMinimum_DoesNotCreateOutboxMessage()
    {
        var response = await PlaceBidAsync("dana", 1249.99m);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await GetOutboxMessageCountAsync());
    }

    [Fact]
    public async Task PlaceBid_WhenScheduledOrClosed_DoesNotCreateOutboxMessage()
    {
        var scheduled = await _client.PostAsJsonAsync($"/api/auctions/{TestAuctionData.ScheduledAuctionId}/bids", new PlaceBidRequest(500m, "dana"), JsonOptions);
        var closed = await _client.PostAsJsonAsync($"/api/auctions/{TestAuctionData.ClosedAuctionId}/bids", new PlaceBidRequest(500m, "dana"), JsonOptions);

        Assert.Equal(HttpStatusCode.Conflict, scheduled.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, closed.StatusCode);
        Assert.Equal(0, await GetOutboxMessageCountAsync());
    }

    [Fact]
    public async Task ConcurrentSameAmount_CreatesOneBidAcceptedOutboxMessage()
    {
        var responses = await Task.WhenAll(
            PlaceBidAsync("alice-2", 1250m),
            PlaceBidAsync("bob-2", 1250m));
        var accepted = responses.Count(r => r.StatusCode == HttpStatusCode.Created);
        var messages = await GetOutboxMessagesAsync();

        Assert.Equal(1, accepted);
        var message = Assert.Single(messages);
        Assert.Equal(IntegrationEventTypes.BidAccepted, message.EventType);
        Assert.Equal(TestAuctionData.OpenAuctionId, message.AggregateId);
        Assert.Equal(4, message.AggregateVersion);
        Assert.Null(message.PublishedAtUtc);
    }

    [Fact]
    public async Task ConcurrentFailedAttempt_DoesNotLeaveOrphanOutboxMessage()
    {
        var responses = await Task.WhenAll(
            PlaceBidAsync("alice-2", 1250m),
            PlaceBidAsync("bob-2", 1250m));
        var accepted = responses.Count(r => r.StatusCode == HttpStatusCode.Created);
        var bids = await GetOpenAuctionBidsAsync();
        var messages = await GetOutboxMessagesAsync();

        Assert.Equal(1, accepted);
        Assert.Equal(3, bids.Count);
        Assert.Single(messages);
        Assert.Equal(bids.Count - 2, messages.Count);
    }

    [Fact]
    public async Task ManyConcurrentBidders_CreatesOneOutboxMessagePerAcceptedBid()
    {
        var requests = new[]
        {
            ("bidder-01", 1250m),
            ("bidder-02", 1250m),
            ("bidder-03", 1300m),
            ("bidder-04", 1350m),
            ("bidder-05", 1300m),
            ("bidder-06", 1400m),
            ("bidder-07", 1450m),
            ("bidder-08", 1500m),
            ("bidder-09", 1400m),
            ("bidder-10", 1500m)
        };

        var responses = await Task.WhenAll(requests.Select(r => PlaceBidAsync(r.Item1, r.Item2)));
        var accepted = responses.Count(r => r.StatusCode == HttpStatusCode.Created);
        var auction = await GetOpenAuctionAsync();
        var bids = await GetOpenAuctionBidsAsync();
        var messages = await GetOutboxMessagesAsync();

        Assert.Equal(accepted, messages.Count);
        Assert.Equal(2 + accepted, bids.Count);
        Assert.All(messages, message =>
        {
            Assert.Equal(IntegrationEventTypes.BidAccepted, message.EventType);
            Assert.Equal(TestAuctionData.OpenAuctionId, message.AggregateId);
            Assert.Null(message.PublishedAtUtc);
        });
        Assert.Equal(auction.Version, messages.Max(m => m.AggregateVersion));
    }
    [Fact]
    public async Task ConcurrentValidBids_SerializeSafely_AndFinalStateUsesHighestAcceptedBid()
    {
        var aliceTask = PlaceBidAsync("alice-2", 1250m);
        await Task.Delay(20);
        var bobTask = PlaceBidAsync("bob-2", 1300m);

        var responses = await Task.WhenAll(aliceTask, bobTask);
        var accepted = responses.Count(r => r.StatusCode == HttpStatusCode.Created);
        var auction = await GetOpenAuctionAsync();
        var bids = await GetOpenAuctionBidsAsync();

        Assert.True(accepted is 1 or 2);
        Assert.Equal(1300m, auction.CurrentBidAmount);
        Assert.Equal("bob-2", auction.CurrentBidderId);
        Assert.Equal(3 + accepted, auction.Version);
        Assert.Equal(2 + accepted, bids.Count);
        Assert.Equal(accepted, bids.Count(b => b.Amount is 1250m or 1300m));
    }

    [Fact]
    public async Task ConcurrentSameAmount_AllowsOnlyOneAcceptedBid()
    {
        var responses = await Task.WhenAll(
            PlaceBidAsync("alice-2", 1250m),
            PlaceBidAsync("bob-2", 1250m));

        var accepted = responses.Where(r => r.StatusCode == HttpStatusCode.Created).ToList();
        var rejected = responses.Where(r => r.StatusCode == HttpStatusCode.BadRequest).ToList();
        var auction = await GetOpenAuctionAsync();
        var bids = await GetOpenAuctionBidsAsync();

        Assert.Single(accepted);
        Assert.Single(rejected);
        Assert.Equal(1250m, auction.CurrentBidAmount);
        Assert.Equal(4, auction.Version);
        Assert.Equal(3, bids.Count);
        Assert.Single(bids, b => b.Amount == 1250m);
    }

    [Fact]
    public async Task ConcurrentLowerBidThatLosesRace_IsRejectedAfterRevalidation()
    {
        var highBidTask = PlaceBidAsync("alice-2", 1300m);
        await Task.Delay(20);
        var lowerBidTask = PlaceBidAsync("bob-2", 1250m);

        var responses = await Task.WhenAll(highBidTask, lowerBidTask);
        var accepted = responses.Where(r => r.StatusCode == HttpStatusCode.Created).ToList();
        var rejected = responses.Where(r => r.StatusCode == HttpStatusCode.BadRequest).ToList();
        var auction = await GetOpenAuctionAsync();
        var bids = await GetOpenAuctionBidsAsync();

        Assert.Single(accepted);
        Assert.Single(rejected);
        Assert.Equal(1300m, auction.CurrentBidAmount);
        Assert.Equal("alice-2", auction.CurrentBidderId);
        Assert.Equal(4, auction.Version);
        Assert.Equal(3, bids.Count);
        Assert.DoesNotContain(bids, b => b.BidderId == "bob-2");
    }

    [Fact]
    public async Task ManyConcurrentBidders_KeepBidHistoryAndVersionConsistent()
    {
        var requests = new[]
        {
            ("bidder-01", 1250m),
            ("bidder-02", 1250m),
            ("bidder-03", 1300m),
            ("bidder-04", 1350m),
            ("bidder-05", 1300m),
            ("bidder-06", 1400m),
            ("bidder-07", 1450m),
            ("bidder-08", 1500m),
            ("bidder-09", 1400m),
            ("bidder-10", 1500m)
        };

        var responses = await Task.WhenAll(requests.Select(r => PlaceBidAsync(r.Item1, r.Item2)));
        var accepted = responses.Count(r => r.StatusCode == HttpStatusCode.Created);
        var auction = await GetOpenAuctionAsync();
        var bids = await GetOpenAuctionBidsAsync();
        var acceptedDemoBids = bids.Where(b => b.BidderId.StartsWith("bidder-", StringComparison.Ordinal)).ToList();

        Assert.NotEmpty(acceptedDemoBids);
        Assert.Equal(acceptedDemoBids.Max(b => b.Amount), auction.CurrentBidAmount);
        Assert.Equal(3 + accepted, auction.Version);
        Assert.Equal(2 + accepted, bids.Count);
        Assert.Equal(accepted, acceptedDemoBids.Count);
        Assert.All(acceptedDemoBids, b => Assert.True(b.Amount >= 1250m));
        Assert.True(accepted is >= 1 and <= 6);
    }

    [Fact]
    public async Task FailedConcurrentAttempt_DoesNotLeavePartialBidRows()
    {
        var responses = await Task.WhenAll(
            PlaceBidAsync("alice-2", 1250m),
            PlaceBidAsync("bob-2", 1250m));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.BadRequest);

        var bids = await GetOpenAuctionBidsAsync();
        Assert.Equal(3, bids.Count);
        Assert.Single(bids, b => b.Amount == 1250m);
        Assert.Equal(1, bids.Count(b => (b.BidderId == "alice-2" || b.BidderId == "bob-2") && b.Amount == 1250m));
    }

    private async Task<List<OutboxMessage>> GetOutboxMessagesAsync()
    {
        return await GetOutboxMessagesAsync(null);
    }

    private async Task<List<OutboxMessage>> GetOutboxMessagesAsync(Guid? auctionId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BiddingDbContext>();
        var query = db.OutboxMessages.AsNoTracking();
        if (auctionId.HasValue)
        {
            query = query.Where(message => message.AggregateId == auctionId.Value);
        }

        return await query.OrderBy(m => m.CreatedAtUtc).ThenBy(m => m.Id).ToListAsync();
    }

    private async Task<int> GetOutboxMessageCountAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BiddingDbContext>();
        return await db.OutboxMessages.CountAsync();
    }

    private Task<HttpResponseMessage> PlaceBidAsync(string bidderId, decimal amount)
    {
        return PlaceBidAsync(TestAuctionData.OpenAuctionId, bidderId, amount, correlationId: null);
    }

    private Task<HttpResponseMessage> PlaceBidAsync(string bidderId, decimal amount, string? correlationId)
    {
        return PlaceBidAsync(TestAuctionData.OpenAuctionId, bidderId, amount, correlationId);
    }

    private Task<HttpResponseMessage> PlaceBidAsync(Guid auctionId, string bidderId, decimal amount)
    {
        return PlaceBidAsync(auctionId, bidderId, amount, correlationId: null);
    }

    private Task<HttpResponseMessage> PlaceBidAsync(
        Guid auctionId,
        string bidderId,
        decimal amount,
        string? correlationId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/auctions/{auctionId}/bids")
        {
            Content = JsonContent.Create(new PlaceBidRequest(amount, bidderId), options: JsonOptions)
        };
        request.Headers.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                JwtTestKeys.CreateToken(bidderId, permissions: ["auction.bid"]));

        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            request.Headers.TryAddWithoutValidation("X-Correlation-ID", correlationId);
        }

        return _client.SendAsync(request);
    }

    private Task<HttpResponseMessage> PlaceBidForTenantAsync(
        Guid auctionId,
        string bidderId,
        decimal amount,
        Guid tenantId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/auctions/{auctionId}/bids")
        {
            Content = JsonContent.Create(new PlaceBidRequest(amount, bidderId), options: JsonOptions)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            JwtTestKeys.CreateToken(bidderId, permissions: ["auction.bid"], tenantId: tenantId.ToString()));
        return _client.SendAsync(request);
    }

    private Task<HttpResponseMessage> CreateAuctionAsync(CreateAuctionRequest request)
    {
        return _client.PostAsJsonAsync("/api/auctions", request, JsonOptions);
    }

    private Task<HttpResponseMessage> UpdateAuctionAsync(Guid auctionId, UpdateAuctionRequest request)
    {
        return _client.PutAsJsonAsync($"/api/auctions/{auctionId}", request, JsonOptions);
    }

    private Task<HttpResponseMessage> CancelAuctionAsync(Guid auctionId, long version)
    {
        return _client.PostAsJsonAsync($"/api/auctions/{auctionId}/cancel", new CancelAuctionRequest(version), JsonOptions);
    }

    private Task<HttpResponseMessage> BuyNowAsync(Guid auctionId, string bidderId, string? correlationId = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/auctions/{auctionId}/buy-now")
        {
            Content = JsonContent.Create(new BuyNowRequest(bidderId), options: JsonOptions)
        };
        request.Headers.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                JwtTestKeys.CreateToken(bidderId, permissions: ["auction.buy"]));

        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            request.Headers.TryAddWithoutValidation("X-Correlation-ID", correlationId);
        }

        return _client.SendAsync(request);
    }

    private async Task<AuctionDetailResponse> GetOpenAuctionAsync()
    {
        return await GetAuctionAsync(TestAuctionData.OpenAuctionId);
    }

    private async Task<AuctionDetailResponse> GetAuctionAsync(Guid auctionId)
    {
        var auction = await _client.GetFromJsonAsync<AuctionDetailResponse>($"/api/auctions/{auctionId}", JsonOptions);
        Assert.NotNull(auction);
        return auction;
    }

    private async Task<List<BidResponse>> GetOpenAuctionBidsAsync()
    {
        return await GetBidsAsync(TestAuctionData.OpenAuctionId);
    }

    private async Task<List<BidResponse>> GetBidsAsync(Guid auctionId)
    {
        var bids = await _client.GetFromJsonAsync<List<BidResponse>>($"/api/auctions/{auctionId}/bids", JsonOptions);
        Assert.NotNull(bids);
        return bids;
    }

    private async Task<Guid> AddBuyNowAuctionAsync(
        SaleMode saleMode,
        decimal startingPrice = 1000m,
        decimal minimumBidIncrement = 50m,
        decimal? buyNowPrice = 1500m,
        decimal? currentBidAmount = null,
        string? currentBidderId = null,
        long version = 1,
        AuctionStatus status = AuctionStatus.Open,
        DateTimeOffset? startTimeUtc = null,
        DateTimeOffset? endTimeUtc = null)
    {
        var auctionId = Guid.NewGuid();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BiddingDbContext>();
        db.Auctions.Add(new Auction
        {
            Id = auctionId,
            TenantId = TenantDefaults.DemoTenantId,
            Title = "BN2 API test auction",
            Description = "BN2 Buy Now integration test.",
            StartingPrice = startingPrice,
            SaleMode = saleMode,
            BuyNowPrice = buyNowPrice,
            MinimumBidIncrement = minimumBidIncrement,
            CurrentBidAmount = currentBidAmount,
            CurrentBidderId = currentBidderId,
            StartTimeUtc = startTimeUtc ?? TestAuctionData.Now.AddHours(-1),
            EndTimeUtc = endTimeUtc ?? TestAuctionData.Now.AddHours(1),
            Status = status,
            Version = version,
            CreatedAtUtc = TestAuctionData.Now.AddDays(-1),
            UpdatedAtUtc = TestAuctionData.Now
        });
        await db.SaveChangesAsync();
        return auctionId;
    }

    private async Task AssertBuyNowRejectedAsync(
        HttpResponseMessage response,
        string expectedCode,
        HttpStatusCode expectedStatus = HttpStatusCode.Conflict)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiErrorResponse>(JsonOptions);
        Assert.Equal(expectedCode, error?.Code);
    }

    private async Task AssertBidRejectedAsync(
        HttpResponseMessage response,
        string expectedCode,
        HttpStatusCode expectedStatus = HttpStatusCode.Conflict)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiErrorResponse>(JsonOptions);
        Assert.Equal(expectedCode, error?.Code);
    }

    private async Task AssertManagementRejectedAsync(
        HttpResponseMessage response,
        string expectedCode,
        HttpStatusCode expectedStatus = HttpStatusCode.Conflict)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiErrorResponse>(JsonOptions);
        Assert.Equal(expectedCode, error?.Code);
    }

    private void UseToken(string subject, Guid tenantId, string[] permissions)
    {
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            JwtTestKeys.CreateToken(subject, permissions: permissions, tenantId: tenantId.ToString()));
    }

    private async Task<Guid> AddTenantAuctionAsync(Guid tenantId, SaleMode saleMode, bool scheduled = false)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BiddingDbContext>();
        await EnsureTenantAsync(db, tenantId);

        var auctionId = Guid.NewGuid();
        db.Auctions.Add(new Auction
        {
            Id = auctionId,
            TenantId = tenantId,
            Title = "Tenant B auction",
            Description = "Cross-tenant authorization fixture.",
            StartingPrice = 1000m,
            SaleMode = saleMode,
            BuyNowPrice = saleMode == SaleMode.BuyNowOnly ? 1500m : null,
            MinimumBidIncrement = 50m,
            StartTimeUtc = scheduled ? TestAuctionData.Now.AddHours(1) : TestAuctionData.Now.AddHours(-1),
            EndTimeUtc = TestAuctionData.Now.AddHours(2),
            Status = scheduled ? AuctionStatus.Scheduled : AuctionStatus.Open,
            Version = 1,
            CreatedAtUtc = TestAuctionData.Now,
            UpdatedAtUtc = TestAuctionData.Now
        });
        await db.SaveChangesAsync();
        return auctionId;
    }

    private async Task EnsureTenantAsync(Guid tenantId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BiddingDbContext>();
        await EnsureTenantAsync(db, tenantId);
        await db.SaveChangesAsync();
    }

    private async Task EnsureTenantStatusAsync(Guid tenantId, TenantStatus status)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BiddingDbContext>();
        var tenant = await db.Tenants.SingleOrDefaultAsync(item => item.Id == tenantId);
        if (tenant is null)
        {
            tenant = Tenant.Create(tenantId, $"Tenant {tenantId}", status, TestAuctionData.Now);
            db.Tenants.Add(tenant);
        }
        else
        {
            tenant.Status = status;
            tenant.UpdatedAtUtc = TestAuctionData.Now;
        }
        await db.SaveChangesAsync();
    }

    private static async Task EnsureTenantAsync(BiddingDbContext db, Guid tenantId)
    {
        if (!await db.Tenants.AnyAsync(tenant => tenant.Id == tenantId))
        {
            db.Tenants.Add(Tenant.Create(tenantId, $"Tenant {tenantId}", TenantStatus.Active, TestAuctionData.Now));
        }
    }

    private static async Task EnsureDemoClientApplicationAsync(BiddingDbContext db)
    {
        await EnsureTenantAsync(db, TenantDefaults.DemoTenantId);
        if (!await db.ClientApplications.AnyAsync(item => item.Id == TenantDefaults.DemoClientApplicationId))
        {
            db.ClientApplications.Add(ClientApplication.Create(
                TenantDefaults.DemoClientApplicationId,
                TenantDefaults.DemoClientId,
                TenantDefaults.DemoTenantId,
                TenantDefaults.DemoClientApplicationName,
                ClientApplicationStatus.Active,
                TestAuctionData.Now));
            await db.SaveChangesAsync();
        }
    }

    private async Task<(long Version, AuctionStatus Status, string Title, decimal? CurrentBidAmount, string? FinalWinnerId)>
        ReadAuctionStateAsync(Guid auctionId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BiddingDbContext>();
        var auction = await db.Auctions.AsNoTracking().SingleAsync(item => item.Id == auctionId);
        return (auction.Version, auction.Status, auction.Title, auction.CurrentBidAmount, auction.FinalWinnerId);
    }

    private async Task<(int Bids, int Outbox)> ReadSideEffectCountsAsync(Guid auctionId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BiddingDbContext>();
        return (
            await db.Bids.CountAsync(bid => bid.AuctionId == auctionId),
            await db.OutboxMessages.CountAsync(message => message.AggregateId == auctionId));
    }
}

public sealed class AuctionApiFactory : WebApplicationFactory<Program>
{
    private readonly FixedTimeProvider _timeProvider = new(TestAuctionData.Now);
    private const string TestConnectionString = "Host=127.0.0.1;Port=55432;Database=auction_demo_tests;Username=auction_app;Password=change_me_in_local_env";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Authentication:BiddingService:Issuer", "dbap-laravel");
        builder.UseSetting("Authentication:BiddingService:Audience", "dbap-bidding-service");
        builder.UseSetting("Authentication:BiddingService:KeyId", "bidding-service-v1");
        builder.UseSetting("Authentication:BiddingService:PublicKeyPath", JwtTestKeys.Path);
        builder.UseSetting("LiveFeedServiceAuthentication:Issuer", "dbap-live-feed-service");
        builder.UseSetting("LiveFeedServiceAuthentication:Subject", "live-feed-service");
        builder.UseSetting("LiveFeedServiceAuthentication:Audience", "dbap-bidding-service");
        builder.UseSetting("LiveFeedServiceAuthentication:KeyId", "live-feed-service-v1");
        builder.UseSetting("LiveFeedServiceAuthentication:PublicKeyPath", JwtTestKeys.Path);
        builder.UseSetting("Authentication:SystemAdmin:Issuer", "dbap-system-admin");
        builder.UseSetting("Authentication:SystemAdmin:Audience", "bidding-service-admin");
        builder.UseSetting("Authentication:SystemAdmin:KeyId", "system-admin-test-v1");
        builder.UseSetting("Authentication:SystemAdmin:PublicKeyPath", JwtTestKeys.Path);
        builder.ConfigureAppConfiguration(config =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:BiddingDb"] = TestConnectionString,
                ["Database:ApplyMigrations"] = "false",
                ["Database:SeedDemoData"] = "false",
                ["BidPlacement:MaxConcurrencyRetries"] = "2",
                ["BidPlacement:ArtificialProcessingDelayMilliseconds"] = "75",
            });
        });

        builder.ConfigureServices(services =>
        {
            services.AddLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddConsole();
            });
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(_timeProvider);
        });
    }

    public async Task ResetDatabaseAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BiddingDbContext>();
        await db.Database.ExecuteSqlRawAsync("DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public;");
        await db.Database.MigrateAsync();
        await TestAuctionData.SeedAsync(db);
    }
}

public sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => utcNow;
}

public static class TestAuctionData
{
    public static readonly DateTimeOffset Now = new(2026, 09, 05, 12, 0, 0, TimeSpan.Zero);
    public static readonly Guid OpenAuctionId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid ScheduledAuctionId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid ClosedAuctionId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    public static readonly Guid EndedOpenAuctionId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    public static readonly Guid FutureOpenAuctionId = Guid.Parse("55555555-5555-5555-5555-555555555555");

    public static async Task SeedAsync(BiddingDbContext db)
    {
        db.Auctions.AddRange(
            new Auction
            {
                Id = OpenAuctionId,
                TenantId = TenantDefaults.DemoTenantId,
                Title = "MacBook Pro",
                Description = "Open test auction.",
                StartingPrice = 1000m,
                SaleMode = SaleMode.AuctionOnly,
                MinimumBidIncrement = 50m,
                CurrentBidAmount = 1200m,
                CurrentBidderId = "carol",
                StartTimeUtc = Now.AddHours(-1),
                EndTimeUtc = Now.AddHours(1),
                Status = AuctionStatus.Open,
                Version = 3,
                CreatedAtUtc = Now.AddDays(-1),
                UpdatedAtUtc = Now.AddMinutes(-5)
            },
            new Auction
            {
                Id = ScheduledAuctionId,
                TenantId = TenantDefaults.DemoTenantId,
                Title = "Camera",
                Description = "Scheduled test auction.",
                StartingPrice = 500m,
                SaleMode = SaleMode.AuctionOnly,
                MinimumBidIncrement = 25m,
                StartTimeUtc = Now.AddHours(1),
                EndTimeUtc = Now.AddHours(2),
                Status = AuctionStatus.Scheduled,
                Version = 1,
                CreatedAtUtc = Now.AddDays(-1),
                UpdatedAtUtc = Now.AddDays(-1)
            },
            new Auction
            {
                Id = ClosedAuctionId,
                TenantId = TenantDefaults.DemoTenantId,
                Title = "Gaming Console",
                Description = "Closed test auction.",
                StartingPrice = 300m,
                SaleMode = SaleMode.AuctionOnly,
                MinimumBidIncrement = 20m,
                CurrentBidAmount = 380m,
                CurrentBidderId = "erin",
                StartTimeUtc = Now.AddDays(-2),
                EndTimeUtc = Now.AddHours(-1),
                Status = AuctionStatus.Closed,
                Version = 2,
                CreatedAtUtc = Now.AddDays(-3),
                UpdatedAtUtc = Now.AddHours(-1)
            },
            new Auction
            {
                Id = EndedOpenAuctionId,
                TenantId = TenantDefaults.DemoTenantId,
                Title = "Ended Open Auction",
                Description = "Status open but end time passed.",
                StartingPrice = 300m,
                SaleMode = SaleMode.AuctionOnly,
                MinimumBidIncrement = 20m,
                StartTimeUtc = Now.AddDays(-1),
                EndTimeUtc = Now.AddMinutes(-1),
                Status = AuctionStatus.Open,
                Version = 1,
                CreatedAtUtc = Now.AddDays(-2),
                UpdatedAtUtc = Now.AddDays(-2)
            },
            new Auction
            {
                Id = FutureOpenAuctionId,
                TenantId = TenantDefaults.DemoTenantId,
                Title = "Future Open Auction",
                Description = "Status open but start time is future.",
                StartingPrice = 300m,
                SaleMode = SaleMode.AuctionOnly,
                MinimumBidIncrement = 20m,
                StartTimeUtc = Now.AddMinutes(1),
                EndTimeUtc = Now.AddHours(1),
                Status = AuctionStatus.Open,
                Version = 1,
                CreatedAtUtc = Now.AddDays(-1),
                UpdatedAtUtc = Now.AddDays(-1)
            });

        db.Bids.AddRange(
            new Bid { Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa1"), AuctionId = OpenAuctionId, BidderId = "alice", Amount = 1000m, CreatedAtUtc = Now.AddMinutes(-40) },
            new Bid { Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa2"), AuctionId = OpenAuctionId, BidderId = "carol", Amount = 1200m, CreatedAtUtc = Now.AddMinutes(-5) });

        await db.SaveChangesAsync();
    }
}

