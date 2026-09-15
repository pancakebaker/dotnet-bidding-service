// <copyright file="ClientAssertionAdmissionOptions.cs" company="Distributed Bidding Auction Platform">
// Copyright (c) Distributed Bidding Auction Platform. Licensed under the MIT license.
// </copyright>
namespace bidding_service.Options;

/// <summary>Controls client assertion admission.</summary>
public sealed class ClientAssertionAdmissionOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "ClientAssertionAdmission";

    /// <summary>Gets or sets whether tenant-facing auction APIs require client assertions.</summary>
    public bool Enabled { get; set; }
}
