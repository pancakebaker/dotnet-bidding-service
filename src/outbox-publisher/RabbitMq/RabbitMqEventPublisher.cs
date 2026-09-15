// <copyright file="RabbitMqEventPublisher.cs" company="Distributed Bidding Auction Platform">
// Copyright (c) Distributed Bidding Auction Platform. Licensed under the MIT license.
// </copyright>
using System.Text;
using DistributedBidding.IntegrationContracts;
using Microsoft.Extensions.Options;
using outbox_publisher.Options;
using outbox_publisher.Outbox;
using RabbitMQ.Client;

namespace outbox_publisher.RabbitMq;

/// <summary>
/// Publishes confirmed integration events to RabbitMQ.
/// </summary>
public sealed class RabbitMqEventPublisher(
    IOptions<RabbitMqOptions> options,
    ILogger<RabbitMqEventPublisher> logger)
{
    /// <summary>
    /// Gets the RabbitMQ exchange used for auction events.
    /// </summary>
    public string Exchange => options.Value.Exchange;
    /// <summary>
    /// Selects the RabbitMQ routing key for an outbox message.
    /// </summary>
    public string RoutingKeyFor(OutboxMessage message) => message.EventType switch
    {
        IntegrationEventTypes.BidAccepted => IntegrationEventRoutingKeys.BidAccepted,
        IntegrationEventTypes.AuctionClosed => IntegrationEventRoutingKeys.AuctionClosed,
        IntegrationEventTypes.WinnerSelected => IntegrationEventRoutingKeys.WinnerSelected,
        IntegrationEventTypes.AuctionPurchased => IntegrationEventRoutingKeys.AuctionPurchased,
        IntegrationEventTypes.AuctionCancelled => IntegrationEventRoutingKeys.AuctionCancelled,
        IntegrationEventTypes.TenantStatusChanged => IntegrationEventRoutingKeys.TenantStatusChanged,
        _ => $"auction.{message.EventType.ToLowerInvariant()}"
    };

    /// <summary>
    /// Publishes one event envelope to RabbitMQ using publisher confirmations.
    /// </summary>
    public async Task PublishAsync(
        OutboxMessage message,
        string envelopeJson,
        CancellationToken cancellationToken)
    {
        await using var connection = await CreateConnectionAsync(cancellationToken);
        await using var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(
                publisherConfirmationsEnabled: true,
                publisherConfirmationTrackingEnabled: true),
            cancellationToken);

        await DeclareTopologyAsync(channel, cancellationToken);

        var properties = new BasicProperties
        {
            MessageId = message.Id.ToString(),
            CorrelationId = message.CorrelationId,
            Type = message.EventType,
            ContentType = "application/json",
            ContentEncoding = "utf-8",
            Persistent = true
        };

        var routingKey = RoutingKeyFor(message);
        var body = Encoding.UTF8.GetBytes(envelopeJson);

        logger.LogInformation(
            "Publishing outbox event {EventId}. EventType: {EventType}, AggregateId: {AggregateId}, CorrelationId: {CorrelationId}",
            message.Id,
            message.EventType,
            message.AggregateId,
            message.CorrelationId);

        await channel.BasicPublishAsync(
            exchange: options.Value.Exchange,
            routingKey: routingKey,
            mandatory: true,
            basicProperties: properties,
            body: body,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Declares the RabbitMQ exchange and optional development debug queue.
    /// </summary>
    public async Task DeclareTopologyAsync(CancellationToken cancellationToken)
    {
        await using var connection = await CreateConnectionAsync(cancellationToken);
        await using var channel = await connection.CreateChannelAsync(
            cancellationToken: cancellationToken);
        await DeclareTopologyAsync(channel, cancellationToken);
    }

    private async Task<IConnection> CreateConnectionAsync(CancellationToken cancellationToken)
    {
        var config = options.Value;
        var factory = new ConnectionFactory
        {
            HostName = config.HostName,
            Port = config.Port,
            UserName = config.UserName,
            Password = config.Password,
            VirtualHost = config.VirtualHost,
            AutomaticRecoveryEnabled = true,
            NetworkRecoveryInterval = TimeSpan.FromSeconds(5),
            ClientProvidedName = "dbap-outbox-publisher"
        };

        return await factory.CreateConnectionAsync(cancellationToken);
    }

    private async Task DeclareTopologyAsync(IChannel channel, CancellationToken cancellationToken)
    {
        await channel.ExchangeDeclareAsync(
            exchange: options.Value.Exchange,
            type: ExchangeType.Topic,
            durable: true,
            autoDelete: false,
            arguments: null,
            cancellationToken: cancellationToken);

        if (options.Value.DeclareDebugQueue)
        {
            await channel.QueueDeclareAsync(
                queue: options.Value.DebugQueue,
                durable: true,
                exclusive: false,
                autoDelete: false,
                arguments: null,
                cancellationToken: cancellationToken);

            await channel.QueueBindAsync(
                queue: options.Value.DebugQueue,
                exchange: options.Value.Exchange,
                routingKey: "auction.#",
                arguments: null,
                cancellationToken: cancellationToken);
        }
    }
}
