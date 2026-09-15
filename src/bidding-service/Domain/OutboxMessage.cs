// <copyright file="OutboxMessage.cs" company="Distributed Bidding Auction Platform">
// Copyright (c) Distributed Bidding Auction Platform. Licensed under the MIT license.
// </copyright>
namespace bidding_service.Domain;

/// <summary>
/// Represents a durable integration event pending publication from PostgreSQL.
/// </summary>
public sealed class OutboxMessage
{
    /// <summary>
    /// Gets or sets the unique identifier.
    /// </summary>
    public Guid Id { get; set; }
    /// <summary>
    /// Gets or sets the integration event type.
    /// </summary>
    public required string EventType { get; set; }
    /// <summary>
    /// Gets or sets the aggregate type that produced the event.
    /// </summary>
    public required string AggregateType { get; set; }
    /// <summary>
    /// Gets or sets the aggregate identifier that produced the event.
    /// </summary>
    public Guid AggregateId { get; set; }
    /// <summary>
    /// Gets or sets the aggregate version produced by the transaction.
    /// </summary>
    public long AggregateVersion { get; set; }
    /// <summary>
    /// Gets or sets the server UTC time when the event occurred.
    /// </summary>
    public DateTimeOffset OccurredAtUtc { get; set; }
    /// <summary>
    /// Gets or sets the correlation identifier for tracing the request or workflow.
    /// </summary>
    public string? CorrelationId { get; set; }
    /// <summary>
    /// Gets or sets the serialized event payload.
    /// </summary>
    public required string Payload { get; set; }
    /// <summary>
    /// Gets or sets the server UTC creation timestamp.
    /// </summary>
    public DateTimeOffset CreatedAtUtc { get; set; }
    /// <summary>
    /// Gets or sets the server UTC time when the event was confirmed by RabbitMQ.
    /// </summary>
    public DateTimeOffset? PublishedAtUtc { get; set; }
    /// <summary>
    /// Gets or sets the number of publisher attempts recorded for the message.
    /// </summary>
    public int PublishAttempts { get; set; }
    /// <summary>
    /// Gets or sets the last concise publisher error, if any.
    /// </summary>
    public string? LastError { get; set; }
}
