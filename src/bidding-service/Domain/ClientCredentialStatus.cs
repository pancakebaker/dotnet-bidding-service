// <copyright file="ClientCredentialStatus.cs" company="Distributed Bidding Auction Platform">
// Copyright (c) Distributed Bidding Auction Platform. Licensed under the MIT license.
// </copyright>
namespace bidding_service.Domain;

/// <summary>Describes the lifecycle state of a client credential.</summary>
public enum ClientCredentialStatus
{
    /// <summary>The credential may be used during its validity window.</summary>
    Active,

    /// <summary>The credential has been permanently revoked.</summary>
    Revoked
}
