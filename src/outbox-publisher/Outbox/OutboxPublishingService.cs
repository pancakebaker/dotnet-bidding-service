// <copyright file="OutboxPublishingService.cs" company="Distributed Bidding Auction Platform">
// Copyright (c) Distributed Bidding Auction Platform. Licensed under the MIT license.
// </copyright>
using Microsoft.Extensions.Options;
using outbox_publisher.Options;

namespace outbox_publisher.Outbox;

/// <summary>
/// Coordinates claiming, publishing, and marking outbox messages.
/// </summary>
public sealed class OutboxPublishingService(
    OutboxStore store,
    RabbitMq.RabbitMqEventPublisher publisher,
    IOptions<PublisherOptions> options,
    ILogger<OutboxPublishingService> logger)
{
    /// <summary>
    /// Claims and publishes one batch of unpublished outbox messages.
    /// </summary>
    public async Task<int> PublishOnceAsync(CancellationToken cancellationToken)
    {
        var batchSize = Math.Max(1, options.Value.BatchSize);
        var maxAttempts = Math.Max(1, options.Value.MaxPublishAttempts);
        await using var batch = await store.ClaimBatchAsync(
            batchSize,
            maxAttempts,
            cancellationToken);

        var published = 0;
        foreach (var message in batch.Messages)
        {
            try
            {
                var envelopeJson = IntegrationEventEnvelope.FromOutboxMessage(message).ToJson();
                await publisher.PublishAsync(message, envelopeJson, cancellationToken);
                await store.MarkPublishedAsync(batch, message, cancellationToken);
                published++;

                logger.LogInformation(
                    "Confirmed outbox event {EventId}. EventType: {EventType}, AggregateId: {AggregateId}, CorrelationId: {CorrelationId}",
                    message.Id,
                    message.EventType,
                    message.AggregateId,
                    message.CorrelationId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await store.MarkPublishFailedAsync(batch, message, ex, cancellationToken);
                logger.LogWarning(
                    ex,
                    "Failed to publish outbox event {EventId}. EventType: {EventType}, AggregateId: {AggregateId}, CorrelationId: {CorrelationId}",
                    message.Id,
                    message.EventType,
                    message.AggregateId,
                    message.CorrelationId);
            }
        }

        await batch.CommitAsync(cancellationToken);
        return published;
    }
}
