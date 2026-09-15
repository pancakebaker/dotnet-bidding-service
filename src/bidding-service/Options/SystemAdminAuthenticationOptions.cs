// <copyright file="SystemAdminAuthenticationOptions.cs" company="Distributed Bidding Auction Platform">
// Copyright (c) Distributed Bidding Auction Platform. Licensed under the MIT license.
// </copyright>
namespace bidding_service.Options;

/// <summary>Configures the Bidding Service trust for system-administrator tokens.</summary>
public sealed class SystemAdminAuthenticationOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Authentication:SystemAdmin";

    /// <summary>Gets or sets the token issuer.</summary>
    public string Issuer { get; set; } = "dbap-system-admin";

    /// <summary>Gets or sets the token audience.</summary>
    public string Audience { get; set; } = "bidding-service-admin";

    /// <summary>Gets or sets the expected key identifier.</summary>
    public string KeyId { get; set; } = "system-admin-development-1";

    /// <summary>Gets or sets the public signing-key path.</summary>
    public string PublicKeyPath { get; set; } = "keys/system-admin-public.pem";
}
