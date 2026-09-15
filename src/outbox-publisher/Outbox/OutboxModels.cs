// <copyright file="OutboxModels.cs" company="Distributed Bidding Auction Platform">
// Copyright (c) Distributed Bidding Auction Platform. Licensed under the MIT license.
// </copyright>
using System.Text.Json;
using System.Text.Json.Nodes;

namespace outbox_publisher.Outbox;

/// <summary>
/// Represents a durable integration event pending publication from PostgreSQL.
/// </summary>
public sealed record OutboxMessage(
    Guid Id,
    string EventType,
    string AggregateType,
    Guid AggregateId,
    long AggregateVersion,
    DateTimeOffset OccurredAtUtc,
    string? CorrelationId,
    string Payload,
    DateTimeOffset CreatedAtUtc,
    int PublishAttempts);

/// <summary>
/// Represents the JSON event envelope published to RabbitMQ.
/// </summary>
public sealed record IntegrationEventEnvelope(
    Guid EventId,
    string EventType,
    DateTimeOffset OccurredAtUtc,
    string AggregateType,
    Guid AggregateId,
    long AggregateVersion,
    string? CorrelationId,
    Guid TenantId,
    JsonNode? Payload)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Builds a publishable event envelope from an outbox row.
    /// </summary>
    public static IntegrationEventEnvelope FromOutboxMessage(OutboxMessage message)
    {
        var payload = JsonNode.Parse(message.Payload)
            ?? throw new InvalidOperationException("Outbox payload cannot be empty.");
        var tenantId = payload["tenantId"]?.GetValue<Guid>()
            ?? throw new InvalidOperationException("Outbox payload is missing tenantId.");

        return new IntegrationEventEnvelope(
            message.Id,
            message.EventType,
            message.OccurredAtUtc,
            message.AggregateType,
            message.AggregateId,
            message.AggregateVersion,
            message.CorrelationId,
            tenantId,
            payload);
    }

    /// <summary>
    /// Serializes the event envelope as UTF-8 JSON content.
    /// </summary>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
}
