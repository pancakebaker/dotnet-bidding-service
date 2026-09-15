// <copyright file="ClientApplicationStatus.cs" company="Distributed Bidding Auction Platform">
// Copyright (c) Distributed Bidding Auction Platform. Licensed under the MIT license.
// </copyright>
namespace bidding_service.Domain;

/// <summary>
/// Describes the administrative state of a registered client application.
/// MT5.1 persists these values; admission enforcement is deferred to later phases.
/// </summary>
public enum ClientApplicationStatus
{
    /// <summary>The application is available for future authentication.</summary>
    Active,
    /// <summary>The application is administratively disabled.</summary>
    Disabled,
    /// <summary>The application trust is revoked and requires explicit reprovisioning.</summary>
    Revoked
}
