using DistributedBidding.IntegrationContracts;
using Microsoft.Extensions.Logging.Abstractions;
using outbox_publisher.Options;
using outbox_publisher.Outbox;
using outbox_publisher.RabbitMq;

namespace outbox_publisher.Tests;

public sealed class RabbitMqRoutingContractTests
{
    [Theory]
    [InlineData("BidAccepted", "auction.bid.accepted")]
    [InlineData("AuctionClosed", "auction.closed")]
    [InlineData("WinnerSelected", "auction.winner.selected")]
    [InlineData("AuctionPurchased", "auction.purchased")]
    [InlineData("AuctionCancelled", "auction.cancelled")]
    [InlineData("TenantStatusChanged", "tenant.status.changed")]
    public void RoutingKeyFor_UsesStableIntegrationRoutingKeys(string eventType, string expectedRoutingKey)
    {
        var publisher = new RabbitMqEventPublisher(
            Microsoft.Extensions.Options.Options.Create(new RabbitMqOptions()),
            NullLogger<RabbitMqEventPublisher>.Instance);
        var message = new OutboxMessage(
            Guid.NewGuid(),
            eventType,
            AggregateTypes.Auction,
            Guid.NewGuid(),
            1,
            DateTimeOffset.UtcNow,
            null,
            "{}",
            DateTimeOffset.UtcNow,
            0);

        Assert.Equal(expectedRoutingKey, publisher.RoutingKeyFor(message));
    }
}
