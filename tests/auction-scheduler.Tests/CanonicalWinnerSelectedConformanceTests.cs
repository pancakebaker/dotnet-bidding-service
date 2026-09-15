using System.Globalization;
using System.Text.Json;
using auction_scheduler.Outbox;
using Json.Schema;
using outbox_publisher.Outbox;

namespace auction_scheduler.Tests;

public sealed class CanonicalWinnerSelectedConformanceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void WinnerSelectedPayload_ValidatesThroughThePublisherEnvelope()
    {
        var tenantId = Guid.Parse("aaaaaaaa-1111-4111-8111-111111111111");
        var auctionId = Guid.Parse("11111111-1111-4111-8111-111111111111");
        var occurredAt = DateTimeOffset.Parse("2026-01-01T00:02:00Z", CultureInfo.InvariantCulture);
        var payload = new WinnerSelectedPayload(
            tenantId,
            auctionId,
            Guid.Parse("22222222-2222-4222-8222-222222222222"),
            "buyer-123",
            1000m,
            occurredAt,
            12);
        var message = new OutboxMessage(
            Guid.Parse("33333333-3333-4333-8333-333333333333"),
            "WinnerSelected",
            "Auction",
            auctionId,
            12,
            occurredAt,
            "winner-correlation",
            JsonSerializer.Serialize(payload, JsonOptions),
            occurredAt,
            0);
        var json = IntegrationEventEnvelope.FromOutboxMessage(message).ToJson();
        var schemaPath = Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "tests", "contracts", "schemas", "v1", "winner-selected.schema.json");
        SchemaRegistry.Global.Register(JsonSchema.FromText(File.ReadAllText(Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "..", "..", "..", "..", "..",
                "tests", "contracts", "schemas", "v1", "event-envelope.schema.json")))));
        var schema = JsonSchema.FromText(File.ReadAllText(Path.GetFullPath(schemaPath)));
        using var document = JsonDocument.Parse(json);

        Assert.True(schema.Evaluate(document.RootElement).IsValid);
    }
}
