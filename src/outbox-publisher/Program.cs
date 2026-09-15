// <copyright file="Program.cs" company="Distributed Bidding Auction Platform">
// Copyright (c) Distributed Bidding Auction Platform. Licensed under the MIT license.
// </copyright>
using Npgsql;
using outbox_publisher;
using outbox_publisher.Options;
using outbox_publisher.Outbox;
using outbox_publisher.RabbitMq;

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration.AddEnvironmentVariables();
builder.Services.Configure<ConnectionStringsOptions>(
    builder.Configuration.GetSection(ConnectionStringsOptions.SectionName));
builder.Services.Configure<RabbitMqOptions>(
    builder.Configuration.GetSection(RabbitMqOptions.SectionName));
builder.Services.PostConfigure<RabbitMqOptions>(options =>
    RabbitMqOptions.ApplyEnvironmentOverrides(options, builder.Configuration));
builder.Services.Configure<PublisherOptions>(
    builder.Configuration.GetSection(PublisherOptions.SectionName));

var connectionString = builder.Configuration.GetConnectionString("BiddingDb")
    ?? throw new InvalidOperationException("ConnectionStrings:BiddingDb is required.");
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton<OutboxStore>();
builder.Services.AddSingleton<RabbitMqEventPublisher>();
builder.Services.AddSingleton<OutboxPublishingService>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
