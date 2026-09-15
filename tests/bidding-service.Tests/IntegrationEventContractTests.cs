using System.Text.Json;
using bidding_service.Domain;
using bidding_service.Services;
using DistributedBidding.IntegrationContracts;

namespace bidding_service.Tests;

public sealed class IntegrationEventContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void WireValuesRemainStable()
    {
        Assert.Equal("BidAccepted", IntegrationEventTypes.BidAccepted);
        Assert.Equal("AuctionClosed", IntegrationEventTypes.AuctionClosed);
        Assert.Equal("WinnerSelected", IntegrationEventTypes.WinnerSelected);
        Assert.Equal("AuctionPurchased", IntegrationEventTypes.AuctionPurchased);
        Assert.Equal("AuctionCancelled", IntegrationEventTypes.AuctionCancelled);
        Assert.Equal("TenantStatusChanged", IntegrationEventTypes.TenantStatusChanged);
        Assert.Equal("Auction", AggregateTypes.Auction);
        Assert.Equal("Tenant", AggregateTypes.Tenant);
        Assert.Equal("auction.bid.accepted", IntegrationEventRoutingKeys.BidAccepted);
        Assert.Equal("auction.closed", IntegrationEventRoutingKeys.AuctionClosed);
        Assert.Equal("auction.winner.selected", IntegrationEventRoutingKeys.WinnerSelected);
        Assert.Equal("auction.purchased", IntegrationEventRoutingKeys.AuctionPurchased);
        Assert.Equal("auction.cancelled", IntegrationEventRoutingKeys.AuctionCancelled);
        Assert.Equal("tenant.status.changed", IntegrationEventRoutingKeys.TenantStatusChanged);
    }

    [Fact]
    public void AuctionPurchasedPayloadAndSiblingEnvelopePreserveAuthoritativeFields()
    {
        var auctionId = Guid.NewGuid();
        var occurredAtUtc = DateTimeOffset.UtcNow;
        var auction = new Auction
        {
            Id = auctionId,
            TenantId = TenantDefaults.DemoTenantId,
            Title = "Contract test auction",
            Description = "Contract test",
            FinalWinnerId = "buyer-123",
            FinalPrice = 1500m,
            Version = 9
        };

        var purchased = OutboxMessageFactory.AuctionPurchased(
            auction,
            "buyer-123",
            "correlation-123",
            occurredAtUtc);
        var closed = OutboxMessageFactory.AuctionClosed(
            auction,
            "correlation-123",
            occurredAtUtc.AddMilliseconds(1));
        var payload = JsonSerializer.Deserialize<AuctionPurchasedPayload>(purchased.Payload, JsonOptions);

        Assert.NotNull(payload);
        Assert.Equal(auctionId, payload.AuctionId);
        Assert.Equal("buyer-123", payload.BidderId);
        Assert.Equal(1500m, payload.FinalPrice);
        Assert.Equal(9, payload.AuctionVersion);
        Assert.Equal(IntegrationEventTypes.AuctionPurchased, purchased.EventType);
        Assert.Equal(IntegrationEventTypes.AuctionClosed, closed.EventType);
        Assert.Equal(auctionId, purchased.AggregateId);
        Assert.Equal(9, purchased.AggregateVersion);
        Assert.Equal(purchased.AggregateVersion, closed.AggregateVersion);
        Assert.Equal(occurredAtUtc, purchased.OccurredAtUtc);
        Assert.Equal("correlation-123", purchased.CorrelationId);
        Assert.Equal("correlation-123", closed.CorrelationId);
        Assert.NotEqual(purchased.Id, closed.Id);
    }

    [Fact]
    public void AuctionCancelledPayloadUsesTheAggregateVersionAndNoTerminalOutcome()
    {
        var auction = new Auction
        {
            Id = Guid.NewGuid(),
            TenantId = TenantDefaults.DemoTenantId,
            Title = "Cancellation contract auction",
            Description = "Contract test",
            Version = 4
        };
        var message = OutboxMessageFactory.AuctionCancelled(
            auction,
            "correlation-cancel",
            DateTimeOffset.UtcNow);
        var payload = JsonSerializer.Deserialize<AuctionCancelledPayload>(message.Payload, JsonOptions);

        Assert.NotNull(payload);
        Assert.Equal(auction.Id, payload.AuctionId);
        Assert.Equal(IntegrationEventTypes.AuctionCancelled, message.EventType);
        Assert.Equal(4, message.AggregateVersion);
    }

    [Fact]
    public void TenantStatusChangedPayloadCarriesTheNewMonotonicVersion()
    {
        var tenant = Tenant.Create(
            Guid.NewGuid(),
            "Contract tenant",
            TenantStatus.Disabled,
            DateTimeOffset.UtcNow);
        tenant.Version = 7;

        var message = OutboxMessageFactory.TenantStatusChanged(
            tenant,
            TenantStatus.Active,
            "correlation-tenant",
            DateTimeOffset.UtcNow);
        using var payload = JsonDocument.Parse(message.Payload);

        Assert.Equal(IntegrationEventTypes.TenantStatusChanged, message.EventType);
        Assert.Equal(AggregateTypes.Tenant, message.AggregateType);
        Assert.Equal(7, message.AggregateVersion);
        Assert.Equal(message.Id, payload.RootElement.GetProperty("eventId").GetGuid());
        Assert.Equal(tenant.Id, payload.RootElement.GetProperty("tenantId").GetGuid());
        Assert.Equal("Active", payload.RootElement.GetProperty("previousStatus").GetString());
        Assert.Equal("Disabled", payload.RootElement.GetProperty("currentStatus").GetString());
        Assert.Equal(7, payload.RootElement.GetProperty("tenantVersion").GetInt64());
    }
}
