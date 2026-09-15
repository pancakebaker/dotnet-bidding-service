// <copyright file="Auction.cs" company="Distributed Bidding Auction Platform">
// Copyright (c) Distributed Bidding Auction Platform. Licensed under the MIT license.
// </copyright>
namespace bidding_service.Domain;

/// <summary>
/// Represents an auction aggregate owned by the bidding service.
/// </summary>
public sealed class Auction
{
    /// <summary>
    /// Gets or sets the unique identifier.
    /// </summary>
    public Guid Id { get; set; }
    /// <summary>
    /// Gets or sets the tenant that owns this auction.
    /// </summary>
    public required Guid TenantId { get; init; }
    /// <summary>
    /// Gets or sets the auction title shown to clients.
    /// </summary>
    public required string Title { get; set; }
    /// <summary>
    /// Gets or sets the auction description shown to clients.
    /// </summary>
    public required string Description { get; set; }
    /// <summary>
    /// Gets or sets the first valid bid amount for the auction.
    /// </summary>
    public decimal StartingPrice { get; set; }
    /// <summary>
    /// Gets or sets how the auction may be completed.
    /// </summary>
    public SaleMode SaleMode { get; set; }
    /// <summary>
    /// Gets or sets the authoritative immediate-purchase price, when enabled.
    /// </summary>
    public decimal? BuyNowPrice { get; set; }
    /// <summary>
    /// Gets or sets the minimum increase required after an accepted bid.
    /// </summary>
    public decimal MinimumBidIncrement { get; set; }
    /// <summary>
    /// Gets or sets the current accepted bid amount, if any.
    /// </summary>
    public decimal? CurrentBidAmount { get; set; }
    /// <summary>
    /// Gets or sets the current highest bidder identity, if any.
    /// </summary>
    public string? CurrentBidderId { get; set; }
    /// <summary>
    /// Gets or sets the terminal winner identity, when the auction has a winner.
    /// </summary>
    public string? FinalWinnerId { get; set; }
    /// <summary>
    /// Gets or sets the terminal sale price, when the auction has a winner.
    /// </summary>
    public decimal? FinalPrice { get; set; }
    /// <summary>
    /// Gets or sets the server UTC time when bidding opens.
    /// </summary>
    public DateTimeOffset StartTimeUtc { get; set; }
    /// <summary>
    /// Gets or sets the server UTC time when bidding closes.
    /// </summary>
    public DateTimeOffset EndTimeUtc { get; set; }
    /// <summary>
    /// Gets or sets the current auction lifecycle state.
    /// </summary>
    public AuctionStatus Status { get; set; }
    /// <summary>
    /// Gets or sets the optimistic concurrency version for the auction aggregate.
    /// </summary>
    public long Version { get; set; }
    /// <summary>
    /// Gets or sets the server UTC creation timestamp.
    /// </summary>
    public DateTimeOffset CreatedAtUtc { get; set; }
    /// <summary>
    /// Gets or sets the server UTC update timestamp.
    /// </summary>
    public DateTimeOffset UpdatedAtUtc { get; set; }
    /// <summary>
    /// Gets or sets the accepted bid history for the auction.
    /// </summary>
    public List<Bid> Bids { get; set; } = [];
}
