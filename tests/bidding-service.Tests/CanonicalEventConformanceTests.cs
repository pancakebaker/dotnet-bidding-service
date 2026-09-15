using System.Globalization;
using System.Text.Json;
using bidding_service.Domain;
using bidding_service.Services;
using outbox_publisher.Outbox;

namespace bidding_service.Tests;

public sealed class CanonicalEventConformanceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Guid TenantId = TenantDefaults.DemoTenantId;
    private static readonly Guid AuctionId = Guid.Parse("11111111-1111-4111-8111-111111111111");

    [Fact]
    public void BiddingLifecycleEvents_ValidateThroughThePublisherEnvelope()
    {
        var auction = new Auction
        {
            Id = AuctionId,
            TenantId = TenantId,
            Title = "Canonical contract auction",
            Description = "Contract conformance",
            FinalWinnerId = "buyer-123",
            FinalPrice = 1000m,
            Version = 12
        };
        var bid = new Bid
        {
            Id = Guid.Parse("22222222-2222-4222-8222-222222222222"),
            AuctionId = AuctionId,
            BidderId = "buyer-123",
            Amount = 1000m,
            CreatedAtUtc = DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture)
        };
        var occurredAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture);

        AssertValid("bid-accepted", OutboxMessageFactory.BidAccepted(
            bid, auction, "contract-correlation", occurredAt));
        AssertValid("auction-purchased", OutboxMessageFactory.AuctionPurchased(
            auction, "buyer-123", "contract-correlation", occurredAt));
        AssertValid("auction-closed", OutboxMessageFactory.AuctionClosed(
            auction, "contract-correlation", occurredAt));
        AssertValid("auction-cancelled", OutboxMessageFactory.AuctionCancelled(
            auction, "contract-correlation", occurredAt));
    }

    [Fact]
    public void TenantStatusChanged_ValidatesThroughTheProducerEnvelope()
    {
        var tenant = Tenant.Create(
            TenantId,
            "Canonical tenant",
            TenantStatus.Disabled,
            DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture));
        tenant.Version = 4;

        AssertValid("tenant-status-changed", OutboxMessageFactory.TenantStatusChanged(
            tenant,
            TenantStatus.Active,
            "tenant-correlation",
            DateTimeOffset.Parse("2026-01-01T00:01:00Z", CultureInfo.InvariantCulture)));
    }

    private static void AssertValid(string schemaName, bidding_service.Domain.OutboxMessage message)
    {
        var published = new outbox_publisher.Outbox.OutboxMessage(
            message.Id,
            message.EventType,
            message.AggregateType,
            message.AggregateId,
            message.AggregateVersion,
            message.OccurredAtUtc,
            message.CorrelationId,
            message.Payload,
            message.CreatedAtUtc,
            message.PublishAttempts);
        var json = IntegrationEventEnvelope.FromOutboxMessage(published).ToJson();
        CanonicalEventSchema.AssertValid(schemaName, json);
    }
}
