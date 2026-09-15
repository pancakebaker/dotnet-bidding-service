// <copyright file="AuctionContracts.cs" company="Distributed Bidding Auction Platform">
// Copyright (c) Distributed Bidding Auction Platform. Licensed under the MIT license.
// </copyright>
namespace bidding_service.Contracts;

/// <summary>
/// Summarizes auction state for list views.
/// </summary>
public sealed record AuctionSummaryResponse(
    Guid Id,
    string Title,
    decimal StartingPrice,
    string SaleMode,
    decimal? BuyNowPrice,
    decimal MinimumBidIncrement,
    decimal? CurrentBidAmount,
    string? CurrentBidderId,
    string? FinalWinnerId,
    decimal? FinalPrice,
    decimal MinimumValidBid,
    string Status,
    DateTimeOffset StartTimeUtc,
    DateTimeOffset EndTimeUtc,
    long Version,
    Guid TenantId);

/// <summary>
/// Returns detailed auction state for the auction detail view.
/// </summary>
public sealed record AuctionDetailResponse(
    Guid Id,
    string Title,
    string Description,
    decimal StartingPrice,
    string SaleMode,
    decimal? BuyNowPrice,
    decimal MinimumBidIncrement,
    decimal? CurrentBidAmount,
    string? CurrentBidderId,
    string? FinalWinnerId,
    decimal? FinalPrice,
    decimal MinimumValidBid,
    string Status,
    DateTimeOffset StartTimeUtc,
    DateTimeOffset EndTimeUtc,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    long Version,
    Guid TenantId);

/// <summary>
/// Describes an accepted bid returned by the bidding API.
/// </summary>
public sealed record BidResponse(
    Guid Id,
    Guid AuctionId,
    string BidderId,
    decimal Amount,
    DateTimeOffset CreatedAtUtc);

/// <summary>
/// Carries the bid amount. The bidder identity comes from the validated principal.
/// BidderId is retained only as a deprecated compatibility field and is ignored.
/// </summary>
public sealed record PlaceBidRequest(decimal Amount, string? BidderId = null);

/// <summary>
/// Carries an explicit Buy Now command. The buyer identity comes from the validated principal.
/// BidderId is retained only as a deprecated compatibility field and is ignored.
/// </summary>
public sealed record BuyNowRequest(string? BidderId = null);

/// <summary>
/// Carries the expected auction version for an explicit cancellation command.
/// </summary>
public sealed record CancelAuctionRequest(long Version);

/// <summary>
/// Describes the accepted bid and resulting auction state.
/// </summary>
public sealed record PlaceBidResponse(
    Guid BidId,
    Guid AuctionId,
    string BidderId,
    decimal Amount,
    decimal CurrentBidAmount,
    string CurrentBidderId,
    decimal NextMinimumBid,
    long AuctionVersion,
    DateTimeOffset CreatedAtUtc,
    string CorrelationId);

/// <summary>
/// Describes a committed Buy Now purchase and resulting auction state.
/// </summary>
public sealed record BuyNowResponse(
    Guid AuctionId,
    string BidderId,
    decimal FinalPrice,
    long AuctionVersion,
    DateTimeOffset PurchasedAtUtc,
    string CorrelationId);

/// <summary>
/// Carries the caller-controlled configuration for a new auction.
/// </summary>
public sealed record CreateAuctionRequest(
    string Title,
    string Description,
    string SaleMode,
    decimal StartingPrice,
    decimal MinimumBidIncrement,
    decimal? BuyNowPrice,
    DateTimeOffset StartTimeUtc,
    DateTimeOffset EndTimeUtc);

/// <summary>
/// Carries editable configuration and the expected aggregate version.
/// </summary>
public sealed record UpdateAuctionRequest(
    string Title,
    string Description,
    string SaleMode,
    decimal StartingPrice,
    decimal MinimumBidIncrement,
    decimal? BuyNowPrice,
    DateTimeOffset StartTimeUtc,
    DateTimeOffset EndTimeUtc,
    long Version);

/// <summary>
/// Provides a stable error shape for API clients.
/// </summary>
public sealed record ApiErrorResponse(string Code, string Message, object? Details = null);

/// <summary>
/// Provides current auction values that help clients correct rejected bids.
/// </summary>
public sealed record BidRuleErrorDetails(
    decimal? CurrentBidAmount,
    decimal MinimumValidBid,
    long AuctionVersion,
    decimal? BuyNowPrice = null);


