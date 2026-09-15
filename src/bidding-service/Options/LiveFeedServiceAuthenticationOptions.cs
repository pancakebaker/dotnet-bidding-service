// <copyright file="LiveFeedServiceAuthenticationOptions.cs" company="Distributed Bidding Auction Platform">
// Copyright (c) Distributed Bidding Auction Platform. Licensed under the MIT license.
// </copyright>
namespace bidding_service.Options;

/// <summary>Configures the trusted Live Feed service identity.</summary>
public sealed class LiveFeedServiceAuthenticationOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "LiveFeedServiceAuthentication";
    /// <summary>Expected issuer.</summary>
    public string Issuer { get; set; } = "dbap-live-feed-service";
    /// <summary>Expected service subject.</summary>
    public string Subject { get; set; } = "live-feed-service";
    /// <summary>Expected audience.</summary>
    public string Audience { get; set; } = "dbap-bidding-service";
    /// <summary>Path to the public verification key.</summary>
    public string PublicKeyPath { get; set; } = "keys/live-feed-service-public.pem";
    /// <summary>Trusted key identifier.</summary>
    public string KeyId { get; set; } = "live-feed-service-v1";
    /// <summary>Maximum accepted token lifetime in seconds.</summary>
    public int MaximumLifetimeSeconds { get; set; } = 30;
}
