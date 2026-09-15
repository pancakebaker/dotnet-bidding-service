// <copyright file="RabbitMqOptions.cs" company="Distributed Bidding Auction Platform">
// Copyright (c) Distributed Bidding Auction Platform. Licensed under the MIT license.
// </copyright>
using Microsoft.Extensions.Configuration;

namespace outbox_publisher.Options;

/// <summary>
/// Configures RabbitMQ connectivity and publisher topology.
/// </summary>
public sealed class RabbitMqOptions
{
    /// <summary>
    /// Identifies the configuration section name.
    /// </summary>
    public const string SectionName = "RabbitMq";
    /// <summary>
    /// Gets or sets the RabbitMQ host name.
    /// </summary>
    public string HostName { get; set; } = "localhost";
    /// <summary>
    /// Gets or sets the RabbitMQ port.
    /// </summary>
    public int Port { get; set; } = 5672;
    /// <summary>
    /// Gets or sets the RabbitMQ username.
    /// </summary>
    public string UserName { get; set; } = "auction";
    /// <summary>
    /// Gets or sets the RabbitMQ password.
    /// </summary>
    public string Password { get; set; } = "change_me_in_local_env";
    /// <summary>
    /// Gets or sets the RabbitMQ virtual host.
    /// </summary>
    public string VirtualHost { get; set; } = "/";
    /// <summary>
    /// Gets the RabbitMQ exchange used for auction events.
    /// </summary>
    public string Exchange { get; set; } = "auction.events";
    /// <summary>
    /// Gets or sets the development debug queue name.
    /// </summary>
    public string DebugQueue { get; set; } = "auction.events.debug";
    /// <summary>
    /// Gets or sets a value indicating whether the development debug queue is declared.
    /// </summary>
    public bool DeclareDebugQueue { get; set; } = true;

    /// <summary>
    /// Applies Outbox Publisher-specific environment overrides after the standard RabbitMq
    /// section is bound.
    /// </summary>
    public static void ApplyEnvironmentOverrides(
        RabbitMqOptions options,
        IConfiguration configuration)
    {
        ApplyString(
            configuration,
            "OUTBOX_PUBLISHER_RABBITMQ_HOST",
            value => options.HostName = value);
        ApplyInt(configuration, "OUTBOX_PUBLISHER_RABBITMQ_PORT", value => options.Port = value);
        ApplyString(
            configuration,
            "OUTBOX_PUBLISHER_RABBITMQ_USERNAME",
            value => options.UserName = value);
        ApplyString(
            configuration,
            "OUTBOX_PUBLISHER_RABBITMQ_PASSWORD",
            value => options.Password = value);
        ApplyString(
            configuration,
            "OUTBOX_PUBLISHER_RABBITMQ_VHOST",
            value => options.VirtualHost = value);
        ApplyString(
            configuration,
            "OUTBOX_PUBLISHER_RABBITMQ_EXCHANGE",
            value => options.Exchange = value);
        ApplyString(
            configuration,
            "OUTBOX_PUBLISHER_RABBITMQ_DEBUG_QUEUE",
            value => options.DebugQueue = value);
        ApplyBool(
            configuration,
            "OUTBOX_PUBLISHER_RABBITMQ_DECLARE_DEBUG_QUEUE",
            value => options.DeclareDebugQueue = value);
    }

    private static void ApplyString(
        IConfiguration configuration,
        string key,
        Action<string> setter)
    {
        var value = configuration[key];
        if (!string.IsNullOrWhiteSpace(value)) setter(value);
    }

    private static void ApplyInt(
        IConfiguration configuration,
        string key,
        Action<int> setter)
    {
        if (int.TryParse(configuration[key], out var value) && value > 0) setter(value);
    }

    private static void ApplyBool(
        IConfiguration configuration,
        string key,
        Action<bool> setter)
    {
        if (bool.TryParse(configuration[key], out var value)) setter(value);
    }
}
