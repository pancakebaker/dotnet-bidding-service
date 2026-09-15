// <copyright file="TenantStatusTransition.cs" company="Distributed Bidding Auction Platform">
// Copyright (c) Distributed Bidding Auction Platform. Licensed under the MIT license.
// </copyright>
namespace bidding_service.Domain;

/// <summary>Durable record of an actual tenant lifecycle transition.</summary>
public sealed class TenantStatusTransition
{
    /// <summary>Gets or sets the transition identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the tenant identifier.</summary>
    public Guid TenantId { get; set; }

    /// <summary>Gets or sets the status before the transition.</summary>
    public TenantStatus PreviousStatus { get; set; }

    /// <summary>Gets or sets the status after the transition.</summary>
    public TenantStatus CurrentStatus { get; set; }

    /// <summary>Gets or sets the committed tenant version.</summary>
    public long TenantVersion { get; set; }

    /// <summary>Gets or sets when the transition was committed.</summary>
    public DateTimeOffset ChangedAtUtc { get; set; }

    /// <summary>Gets or sets the SystemAdministrator subject that made the change.</summary>
    public required string ChangedBySubject { get; set; }

    /// <summary>Gets or sets the request correlation identifier.</summary>
    public required string CorrelationId { get; set; }
}
