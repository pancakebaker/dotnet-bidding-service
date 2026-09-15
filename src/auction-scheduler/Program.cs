// <copyright file="Program.cs" company="Distributed Bidding Auction Platform">
// Copyright (c) Distributed Bidding Auction Platform. Licensed under the MIT license.
// </copyright>
using auction_scheduler;
using auction_scheduler.Options;
using Npgsql;

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration.AddEnvironmentVariables();
builder.Services.Configure<ConnectionStringsOptions>(
    builder.Configuration.GetSection(ConnectionStringsOptions.SectionName));
builder.Services.Configure<SchedulerOptions>(
    builder.Configuration.GetSection(SchedulerOptions.SectionName));

var connectionString = builder.Configuration.GetConnectionString("BiddingDb")
    ?? throw new InvalidOperationException("ConnectionStrings:BiddingDb is required.");

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton<AuctionClosingService>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
