// <copyright file="BiddingAuthenticationOptions.cs" company="Distributed Bidding Auction Platform">
// Copyright (c) Distributed Bidding Auction Platform. Licensed under the MIT license.
// </copyright>
namespace bidding_service.Options;

/// <summary>Configures validation of trusted Laravel identity tokens.</summary>
public sealed class BiddingAuthenticationOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Authentication:BiddingService";

    /// <summary>Expected token issuer.</summary>
    public string Issuer { get; set; } = "dbap-laravel";

    /// <summary>Expected token audience.</summary>
    public string Audience { get; set; } = "dbap-bidding-service";

    /// <summary>Path to the public RSA verification key.</summary>
    public string PublicKeyPath { get; set; } = "keys/bidding-service-public.pem";

    /// <summary>Identifier of the active public RSA verification key.</summary>
    public string KeyId { get; set; } = "bidding-service-v1";
}
