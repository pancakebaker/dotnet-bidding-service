// <copyright file="Bid.cs" company="Distributed Bidding Auction Platform">
// Copyright (c) Distributed Bidding Auction Platform. Licensed under the MIT license.
// </copyright>
namespace bidding_service.Domain;

/// <summary>
/// Represents an accepted bid recorded for an auction.
/// </summary>
public sealed class Bid
{
    /// <summary>
    /// Gets or sets the unique identifier.
    /// </summary>
    public Guid Id { get; set; }
    /// <summary>
    /// Gets or sets the auction identifier associated with the record.
    /// </summary>
    public Guid AuctionId { get; set; }
    /// <summary>
    /// Gets or sets the bidder identity associated with the bid.
    /// </summary>
    public required string BidderId { get; set; }
    /// <summary>
    /// Gets or sets the monetary bid amount.
    /// </summary>
    public decimal Amount { get; set; }
    /// <summary>
    /// Gets or sets the server UTC creation timestamp.
    /// </summary>
    public DateTimeOffset CreatedAtUtc { get; set; }
    /// <summary>
    /// Gets or sets the auction.
    /// </summary>
    public Auction? Auction { get; set; }
}
