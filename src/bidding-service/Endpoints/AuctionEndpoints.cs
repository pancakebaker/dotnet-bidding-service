// <copyright file="AuctionEndpoints.cs" company="Distributed Bidding Auction Platform">
// Copyright (c) Distributed Bidding Auction Platform. Licensed under the MIT license.
// </copyright>
using bidding_service.Contracts;
using bidding_service.Data;
using bidding_service.Domain;
using bidding_service.Security.ClientAssertions;
using bidding_service.Services;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace bidding_service.Endpoints;

/// <summary>
/// Maps auction discovery, detail, history, and bid placement endpoints.
/// </summary>
public static class AuctionEndpoints
{
    private const string CorrelationIdHeader = "X-Correlation-ID";

    /// <summary>
    /// Registers auction and bid endpoints on the web application.
    /// </summary>
    public static RouteGroupBuilder MapAuctionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auctions")
            .WithTags("Auctions")
            .AddEndpointFilter<ClientAssertionAdmissionFilter>();

        group.MapPost("/", CreateAuction)
            .WithName("CreateAuction")
            .WithMetadata(new TenantRuntimeAccessMetadata(TenantRuntimeOperation.Mutate))
            .AddEndpointFilter<TenantRuntimeStatusFilter>()
            .RequireAuthorization("AuctionManage")
            .Produces<AuctionDetailResponse>(StatusCodes.Status201Created)
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorResponse>(StatusCodes.Status409Conflict);

        group.MapPut("/{id:guid}", UpdateAuction)
            .WithName("UpdateAuction")
            .WithMetadata(new TenantRuntimeAccessMetadata(TenantRuntimeOperation.Mutate))
            .AddEndpointFilter<TenantRuntimeStatusFilter>()
            .RequireAuthorization("AuctionManage")
            .Produces<AuctionDetailResponse>()
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ApiErrorResponse>(StatusCodes.Status409Conflict);

        group.MapDelete("/{id:guid}", DeleteAuction)
            .WithName("DeleteAuction")
            .WithMetadata(new TenantRuntimeAccessMetadata(TenantRuntimeOperation.Mutate))
            .AddEndpointFilter<TenantRuntimeStatusFilter>()
            .RequireAuthorization("AuctionManage")
            .Produces(StatusCodes.Status204NoContent)
            .Produces<ApiErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ApiErrorResponse>(StatusCodes.Status409Conflict);

        group.MapPost("/{id:guid}/cancel", CancelAuction)
            .WithName("CancelAuction")
            .WithMetadata(new TenantRuntimeAccessMetadata(TenantRuntimeOperation.Mutate))
            .AddEndpointFilter<TenantRuntimeStatusFilter>()
            .RequireAuthorization("AuctionManage")
            .Produces<AuctionDetailResponse>()
            .Produces<ApiErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ApiErrorResponse>(StatusCodes.Status409Conflict);

        group.MapGet("/", GetAuctions)
            .WithName("GetAuctions")
            .WithMetadata(new TenantRuntimeAccessMetadata(TenantRuntimeOperation.Read))
            .AddEndpointFilter<TenantRuntimeStatusFilter>()
            .RequireAuthorization("AuctionRead")
            .Produces<IReadOnlyList<AuctionSummaryResponse>>();

        group.MapGet("/{id:guid}", GetAuction)
            .WithName("GetAuction")
            .WithMetadata(new TenantRuntimeAccessMetadata(TenantRuntimeOperation.Read))
            .AddEndpointFilter<TenantRuntimeStatusFilter>()
            .RequireAuthorization("AuctionRead")
            .Produces<AuctionDetailResponse>()
            .Produces<ApiErrorResponse>(StatusCodes.Status404NotFound);

        group.MapGet("/{id:guid}/bids", GetBids)
            .WithName("GetAuctionBids")
            .WithMetadata(new TenantRuntimeAccessMetadata(TenantRuntimeOperation.Read))
            .AddEndpointFilter<TenantRuntimeStatusFilter>()
            .RequireAuthorization("AuctionRead")
            .Produces<IReadOnlyList<BidResponse>>()
            .Produces<ApiErrorResponse>(StatusCodes.Status404NotFound);

        group.MapPost("/{id:guid}/bids", PlaceBid)
            .WithName("PlaceBid")
            .WithMetadata(new TenantRuntimeAccessMetadata(TenantRuntimeOperation.Mutate))
            .AddEndpointFilter<TenantRuntimeStatusFilter>()
            .RequireAuthorization("AuctionBid")
            .Produces<PlaceBidResponse>(StatusCodes.Status201Created)
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ApiErrorResponse>(StatusCodes.Status409Conflict);

        group.MapPost("/{id:guid}/buy-now", BuyNow)
            .WithName("BuyNow")
            .WithMetadata(new TenantRuntimeAccessMetadata(TenantRuntimeOperation.Mutate))
            .AddEndpointFilter<TenantRuntimeStatusFilter>()
            .RequireAuthorization("AuctionBuy")
            .Produces<BuyNowResponse>(StatusCodes.Status201Created)
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ApiErrorResponse>(StatusCodes.Status409Conflict);

        return group;
    }

    private static async Task<IResult> CreateAuction(
        CreateAuctionRequest request,
        BiddingDbContext db,
        TimeProvider timeProvider,
        ITenantIdentityAccessor tenantIdentityAccessor,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (!tenantIdentityAccessor.TryGetTenantId(httpContext.User, out var tenantId))
        {
            return TypedResults.Forbid();
        }

        if (!await db.Tenants.AnyAsync(tenant => tenant.Id == tenantId, cancellationToken))
        {
            return TypedResults.NotFound(new ApiErrorResponse("tenant_not_found", "Tenant not found."));
        }

        var now = timeProvider.GetUtcNow();
        var basicError = ValidateBasicFields(request.Title, request.Description);
        if (basicError is not null)
        {
            return TypedResults.BadRequest(basicError);
        }

        var timeError = ValidateCreationWindow(request.StartTimeUtc, request.EndTimeUtc, now);
        if (timeError is not null)
        {
            return TypedResults.BadRequest(timeError);
        }

        if (!AuctionManagementRules.TryNormalize(
                request.SaleMode,
                request.StartingPrice,
                request.MinimumBidIncrement,
                request.BuyNowPrice,
                out var configuration,
                out var configurationError))
        {
            return TypedResults.BadRequest(configurationError!);
        }

        var auction = new Auction
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Title = request.Title.Trim(),
            Description = request.Description.Trim(),
            StartingPrice = configuration.StartingPrice,
            SaleMode = configuration.SaleMode,
            BuyNowPrice = configuration.BuyNowPrice,
            MinimumBidIncrement = configuration.MinimumBidIncrement,
            StartTimeUtc = request.StartTimeUtc,
            EndTimeUtc = request.EndTimeUtc,
            Status = request.StartTimeUtc > now ? AuctionStatus.Scheduled : AuctionStatus.Open,
            Version = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        db.Auctions.Add(auction);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Created($"/api/auctions/{auction.Id}", ToDetailResponse(auction));
    }

    private static async Task<IResult> UpdateAuction(
        Guid id,
        UpdateAuctionRequest request,
        BiddingDbContext db,
        TimeProvider timeProvider,
        ITenantIdentityAccessor tenantIdentityAccessor,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (!tenantIdentityAccessor.TryGetTenantId(httpContext.User, out var tenantId))
        {
            return TypedResults.Forbid();
        }

        var auction = await db.Auctions.FirstOrDefaultAsync(
            a => a.Id == id && a.TenantId == tenantId,
            cancellationToken);
        if (auction is null)
        {
            return TypedResults.NotFound(new ApiErrorResponse("auction_not_found", "Auction not found."));
        }

        var now = timeProvider.GetUtcNow();
        if (auction.Status == AuctionStatus.Scheduled && now >= auction.StartTimeUtc)
        {
            return TypedResults.Conflict(new ApiErrorResponse(
                "auction_already_started",
                "The scheduled auction has already reached its start time."));
        }

        if (auction.Status != AuctionStatus.Scheduled)
        {
            return TypedResults.Conflict(new ApiErrorResponse(
                "auction_not_editable",
                "Only future Scheduled auctions can be edited."));
        }

        if (await db.Bids.AnyAsync(b => b.AuctionId == id, cancellationToken))
        {
            return TypedResults.Conflict(new ApiErrorResponse(
                "auction_not_editable",
                "An auction with bid history cannot be edited."));
        }

        if (request.Version != auction.Version)
        {
            return TypedResults.Conflict(new ApiErrorResponse(
                "auction_concurrency_conflict",
                "Auction state changed. Refresh before editing again."));
        }

        var basicError = ValidateBasicFields(request.Title, request.Description);
        if (basicError is not null)
        {
            return TypedResults.BadRequest(basicError);
        }

        var timeError = ValidateUpdateWindow(request.StartTimeUtc, request.EndTimeUtc, now);
        if (timeError is not null)
        {
            return TypedResults.BadRequest(timeError);
        }

        if (!AuctionManagementRules.TryNormalize(
                request.SaleMode,
                request.StartingPrice,
                request.MinimumBidIncrement,
                request.BuyNowPrice,
                out var configuration,
                out var configurationError))
        {
            return TypedResults.BadRequest(configurationError!);
        }

        auction.Title = request.Title.Trim();
        auction.Description = request.Description.Trim();
        auction.StartingPrice = configuration.StartingPrice;
        auction.SaleMode = configuration.SaleMode;
        auction.BuyNowPrice = configuration.BuyNowPrice;
        auction.MinimumBidIncrement = configuration.MinimumBidIncrement;
        auction.StartTimeUtc = request.StartTimeUtc;
        auction.EndTimeUtc = request.EndTimeUtc;
        auction.Version += 1;
        auction.UpdatedAtUtc = now;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return TypedResults.Conflict(new ApiErrorResponse(
                "auction_concurrency_conflict",
                "Auction state changed. Refresh before editing again."));
        }

        return TypedResults.Ok(ToDetailResponse(auction));
    }

    private static async Task<IResult> DeleteAuction(
        Guid id,
        BiddingDbContext db,
        TimeProvider timeProvider,
        ITenantIdentityAccessor tenantIdentityAccessor,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (!tenantIdentityAccessor.TryGetTenantId(httpContext.User, out var tenantId))
        {
            return TypedResults.Forbid();
        }

        var auction = await db.Auctions.FirstOrDefaultAsync(
            a => a.Id == id && a.TenantId == tenantId,
            cancellationToken);
        if (auction is null)
        {
            return TypedResults.NotFound(new ApiErrorResponse("auction_not_found", "Auction not found."));
        }

        var now = timeProvider.GetUtcNow();
        var hasBids = await db.Bids.AnyAsync(b => b.AuctionId == id, cancellationToken);
        var hasEvents = await db.OutboxMessages.AnyAsync(m => m.AggregateId == id, cancellationToken);
        var safelyDeletable = auction.Status == AuctionStatus.Scheduled
            && now < auction.StartTimeUtc
            && !hasBids
            && !hasEvents
            && auction.CurrentBidAmount is null
            && auction.CurrentBidderId is null
            && auction.FinalWinnerId is null
            && auction.FinalPrice is null;

        if (!safelyDeletable)
        {
            return TypedResults.Conflict(new ApiErrorResponse(
                "auction_not_deletable",
                "Only untouched future Scheduled auctions can be deleted."));
        }

        db.Auctions.Remove(auction);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return TypedResults.Conflict(new ApiErrorResponse(
                "auction_concurrency_conflict",
                "Auction state changed while it was being deleted."));
        }

        return TypedResults.NoContent();
    }

    private static async Task<IResult> CancelAuction(
        Guid id,
        CancelAuctionRequest request,
        BiddingDbContext db,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory,
        ITenantIdentityAccessor tenantIdentityAccessor,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (!tenantIdentityAccessor.TryGetTenantId(httpContext.User, out var tenantId))
        {
            return TypedResults.Forbid();
        }

        var logger = loggerFactory.CreateLogger("AuctionCancellation");
        var correlationId = ResolveCorrelationId(httpContext);
        httpContext.Response.Headers[CorrelationIdHeader] = correlationId;

        var auction = await db.Auctions.FirstOrDefaultAsync(
            a => a.Id == id && a.TenantId == tenantId,
            cancellationToken);
        if (auction is null)
        {
            return TypedResults.NotFound(new ApiErrorResponse("auction_not_found", "Auction not found."));
        }

        var now = timeProvider.GetUtcNow();
        if (auction.Status == AuctionStatus.Cancelled)
        {
            return TypedResults.Conflict(new ApiErrorResponse(
                "auction_already_cancelled",
                "Auction is already cancelled."));
        }

        if (auction.Status is AuctionStatus.Closed)
        {
            return TypedResults.Conflict(new ApiErrorResponse(
                "auction_not_cancellable",
                "Closed or purchased auctions cannot be cancelled."));
        }

        if (request.Version != auction.Version)
        {
            return TypedResults.Conflict(new ApiErrorResponse(
                "auction_concurrency_conflict",
                "Auction state changed. Refresh before cancelling again."));
        }

        auction.Status = AuctionStatus.Cancelled;
        auction.Version += 1;
        auction.UpdatedAtUtc = now;
        var cancelledMessage = OutboxMessageFactory.AuctionCancelled(auction, correlationId, now);
        db.OutboxMessages.Add(cancelledMessage);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            logger.LogInformation(
                "Cancelled auction {AuctionId}. Version advanced to {AuctionVersion}. EventId: {EventId}. CorrelationId: {CorrelationId}",
                auction.Id,
                auction.Version,
                cancelledMessage.Id,
                correlationId);
            return TypedResults.Ok(ToDetailResponse(auction));
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            logger.LogWarning(
                "Auction cancellation concurrency conflict for {AuctionId}. CorrelationId: {CorrelationId}",
                id,
                correlationId);
            return TypedResults.Conflict(new ApiErrorResponse(
                "auction_concurrency_conflict",
                "Auction state changed. Refresh before cancelling again."));
        }
    }

    private static ApiErrorResponse? ValidateBasicFields(string? title, string? description)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Trim().Length > 200)
        {
            return new ApiErrorResponse("invalid_auction_configuration", "Title is required and must be at most 200 characters.");
        }

        if (string.IsNullOrWhiteSpace(description) || description.Trim().Length > 2000)
        {
            return new ApiErrorResponse("invalid_auction_configuration", "Description is required and must be at most 2000 characters.");
        }

        return null;
    }

    private static ApiErrorResponse? ValidateCreationWindow(
        DateTimeOffset startTimeUtc,
        DateTimeOffset endTimeUtc,
        DateTimeOffset now)
    {
        if (startTimeUtc >= endTimeUtc || endTimeUtc <= now)
        {
            return new ApiErrorResponse(
                "invalid_auction_time_window",
                "StartTimeUtc must be before EndTimeUtc and EndTimeUtc must be in the future.");
        }

        return null;
    }

    private static ApiErrorResponse? ValidateUpdateWindow(
        DateTimeOffset startTimeUtc,
        DateTimeOffset endTimeUtc,
        DateTimeOffset now)
    {
        if (startTimeUtc <= now || startTimeUtc >= endTimeUtc || endTimeUtc <= now)
        {
            return new ApiErrorResponse(
                "invalid_auction_time_window",
                "Scheduled edits must keep StartTimeUtc in the future and before EndTimeUtc.");
        }

        return null;
    }

    private static async Task<IResult> GetAuctions(
        BiddingDbContext db,
        ITenantIdentityAccessor tenantIdentityAccessor,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (!tenantIdentityAccessor.TryGetTenantId(httpContext.User, out var tenantId))
        {
            return TypedResults.Forbid();
        }
        var auctions = await db.Auctions
            .AsNoTracking()
            .Where(a => a.TenantId == tenantId)
            .OrderBy(a => a.StartTimeUtc)
            .Select(a => new AuctionSummaryResponse(
                a.Id,
                a.Title,
                a.StartingPrice,
                a.SaleMode.ToString(),
                a.BuyNowPrice,
                a.MinimumBidIncrement,
                a.CurrentBidAmount,
                a.CurrentBidderId,
                a.FinalWinnerId,
                a.FinalPrice,
                a.CurrentBidAmount == null ? a.StartingPrice : a.CurrentBidAmount.Value + a.MinimumBidIncrement,
                a.Status.ToString(),
                a.StartTimeUtc,
                a.EndTimeUtc,
                a.Version,
                a.TenantId))
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(auctions);
    }

    private static async Task<
        Results<Ok<AuctionDetailResponse>, NotFound<ApiErrorResponse>>>
        GetAuction(
        Guid id,
        BiddingDbContext db,
        ITenantIdentityAccessor tenantIdentityAccessor,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (!tenantIdentityAccessor.TryGetTenantId(httpContext.User, out var tenantId))
        {
            return TypedResults.NotFound(new ApiErrorResponse("auction_not_found", "Auction not found."));
        }
        var auction = await db.Auctions
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == id && a.TenantId == tenantId, cancellationToken);
        if (auction is null)
        {
            return TypedResults.NotFound(
                new ApiErrorResponse("auction_not_found", "Auction not found."));
        }

        return TypedResults.Ok(ToDetailResponse(auction));
    }

    private static async Task<Results<Ok<List<BidResponse>>, NotFound<ApiErrorResponse>>> GetBids(
        Guid id,
        BiddingDbContext db,
        ITenantIdentityAccessor tenantIdentityAccessor,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (!tenantIdentityAccessor.TryGetTenantId(httpContext.User, out var tenantId))
        {
            return TypedResults.NotFound(new ApiErrorResponse("auction_not_found", "Auction not found."));
        }
        var auctionExists = await db.Auctions.AnyAsync(a => a.Id == id && a.TenantId == tenantId, cancellationToken);
        if (!auctionExists)
        {
            return TypedResults.NotFound(
                new ApiErrorResponse("auction_not_found", "Auction not found."));
        }

        var bids = await db.Bids
            .AsNoTracking()
            .Where(b => b.AuctionId == id)
            .OrderByDescending(b => b.CreatedAtUtc)
            .Select(b => new BidResponse(b.Id, b.AuctionId, b.BidderId, b.Amount, b.CreatedAtUtc))
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(bids);
    }

    private static async Task<IResult>
        PlaceBid(
        Guid id,
        PlaceBidRequest request,
        BiddingDbContext db,
        TimeProvider timeProvider,
        IOptions<BidPlacementOptions> options,
        ILoggerFactory loggerFactory,
        IBuyerIdentityResolver identityResolver,
        ITenantIdentityAccessor tenantIdentityAccessor,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (!tenantIdentityAccessor.TryGetTenantId(httpContext.User, out var tenantId))
        {
            return TypedResults.Forbid();
        }

        var logger = loggerFactory.CreateLogger("BidPlacement");
        var correlationId = ResolveCorrelationId(httpContext);
        httpContext.Response.Headers[CorrelationIdHeader] = correlationId;

        var bidderId = identityResolver.Resolve(httpContext, null);
        if (string.IsNullOrWhiteSpace(bidderId))
        {
            return TypedResults.BadRequest(
                new ApiErrorResponse("invalid_bidder", "BidderId is required."));
        }

        if (request.Amount <= 0)
        {
            return TypedResults.BadRequest(
                new ApiErrorResponse(
                    "invalid_bid_amount",
                    "Bid amount must be greater than zero."));
        }

        var maxRetries = Math.Max(0, options.Value.MaxConcurrencyRetries);
        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            await using var transaction = await db.Database
                .BeginTransactionAsync(cancellationToken);
            var auction = await db.Auctions.FirstOrDefaultAsync(
                a => a.Id == id && a.TenantId == tenantId,
                cancellationToken);
            if (auction is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return TypedResults.NotFound(
                    new ApiErrorResponse("auction_not_found", "Auction not found."));
            }

            var now = timeProvider.GetUtcNow();
            var validationError = ValidateBid(auction, request.Amount, now);
            if (validationError is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                logger.LogInformation(
                    "Bid rejected after validation for auction {AuctionId}. Code: {Code}. Version: {AuctionVersion}. CorrelationId: {CorrelationId}",
                    auction.Id,
                    validationError.Error.Code,
                    auction.Version,
                    correlationId);

                return validationError.StatusCode == StatusCodes.Status409Conflict
                    ? TypedResults.Conflict(validationError.Error)
                    : TypedResults.BadRequest(validationError.Error);
            }

            var isBuyNowThreshold = auction.SaleMode == SaleMode.AuctionAndBuyNow
                && auction.BuyNowPrice.HasValue
                && request.Amount >= auction.BuyNowPrice.Value;
            var acceptedAmount = isBuyNowThreshold
                ? auction.BuyNowPrice!.Value
                : request.Amount;

            if (options.Value.ArtificialProcessingDelayMilliseconds > 0)
            {
                await Task.Delay(
                    options.Value.ArtificialProcessingDelayMilliseconds,
                    cancellationToken);
            }

            var bid = new Bid
            {
                Id = Guid.NewGuid(),
                AuctionId = auction.Id,
                BidderId = bidderId,
                Amount = acceptedAmount,
                CreatedAtUtc = now
            };

            db.Bids.Add(bid);
            auction.CurrentBidAmount = acceptedAmount;
            auction.CurrentBidderId = bid.BidderId;

            if (isBuyNowThreshold)
            {
                auction.FinalWinnerId = bidderId;
                auction.FinalPrice = acceptedAmount;
                auction.Status = AuctionStatus.Closed;
            }

            auction.Version += 1;
            auction.UpdatedAtUtc = now;

            var outboxMessage = OutboxMessageFactory.BidAccepted(bid, auction, correlationId, now);
            db.OutboxMessages.Add(outboxMessage);

            OutboxMessage? purchasedMessage = null;
            OutboxMessage? closedMessage = null;
            if (isBuyNowThreshold)
            {
                purchasedMessage = OutboxMessageFactory.AuctionPurchased(
                    auction,
                    bidderId,
                    correlationId,
                    now);
                closedMessage = OutboxMessageFactory.AuctionClosed(
                    auction,
                    correlationId,
                    now.AddMilliseconds(1));
                db.OutboxMessages.AddRange(purchasedMessage, closedMessage);
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);

                logger.LogInformation(
                    "Accepted bid for auction {AuctionId}. BuyNowThreshold: {BuyNowThreshold}. Version advanced to {AuctionVersion}. OutboxMessageId: {OutboxMessageId}. CorrelationId: {CorrelationId}",
                    auction.Id,
                    isBuyNowThreshold,
                    auction.Version,
                    outboxMessage.Id,
                    correlationId);

                var response = new PlaceBidResponse(
                    bid.Id,
                    auction.Id,
                    bid.BidderId,
                    bid.Amount,
                    auction.CurrentBidAmount.Value,
                    auction.CurrentBidderId!,
                    BidRules.GetMinimumValidBid(auction),
                    auction.Version,
                    bid.CreatedAtUtc,
                    correlationId);

                return TypedResults.Created($"/api/auctions/{auction.Id}/bids/{bid.Id}", response);
            }
            catch (DbUpdateConcurrencyException)
            {
                await transaction.RollbackAsync(cancellationToken);
                db.ChangeTracker.Clear();

                logger.LogWarning(
                    "Concurrency conflict detected for auction {AuctionId} on attempt {Attempt} of {MaxAttempts}. CorrelationId: {CorrelationId}",
                    id,
                    attempt + 1,
                    maxRetries + 1,
                    correlationId);

                if (attempt == maxRetries)
                {
                    var currentAuction = await db.Auctions
                        .AsNoTracking()
                        .FirstOrDefaultAsync(
                            a => a.Id == id && a.TenantId == tenantId,
                            cancellationToken);
                    return TypedResults.Conflict(new ApiErrorResponse(
                        "auction_concurrency_conflict",
                        "Auction state changed while the bid was being placed. Retry with the latest auction state.",
                        currentAuction is null ? null : ToBidRuleDetails(currentAuction)));
                }

                logger.LogInformation(
                    "Retrying bid placement for auction {AuctionId}. Next attempt: {Attempt}. CorrelationId: {CorrelationId}",
                    id,
                    attempt + 2,
                    correlationId);
            }
        }

        return TypedResults.Conflict(new ApiErrorResponse(
            "auction_concurrency_conflict",
            "Auction state changed while the bid was being placed. Retry with the latest auction state."));
    }

    private static BidValidationError? ValidateBid(
        Auction auction,
        decimal amount,
        DateTimeOffset now)
    {
        if (auction.Status != AuctionStatus.Open)
        {
            return new BidValidationError(
                StatusCodes.Status409Conflict,
                new ApiErrorResponse(
                    "auction_not_open",
                    "Auction is not open for bidding.",
                    ToBidRuleDetails(auction)));
        }

        if (now < auction.StartTimeUtc)
        {
            return new BidValidationError(
                StatusCodes.Status409Conflict,
                new ApiErrorResponse(
                    "auction_not_started",
                    "Auction has not started yet.",
                    ToBidRuleDetails(auction)));
        }

        if (now >= auction.EndTimeUtc)
        {
            return new BidValidationError(
                StatusCodes.Status409Conflict,
                new ApiErrorResponse(
                    "auction_ended",
                    "Auction has already ended.",
                    ToBidRuleDetails(auction)));
        }

        if (auction.SaleMode == SaleMode.BuyNowOnly)
        {
            return new BidValidationError(
                StatusCodes.Status409Conflict,
                new ApiErrorResponse(
                    "bidding_not_available",
                    "Ordinary bidding is not available for this auction.",
                    ToBidRuleDetails(auction)));
        }

        if (auction.SaleMode == SaleMode.AuctionAndBuyNow)
        {
            if (auction.BuyNowPrice is null || auction.BuyNowPrice <= 0)
            {
                return new BidValidationError(
                    StatusCodes.Status409Conflict,
                    new ApiErrorResponse(
                        "buy_now_not_available",
                        "Buy Now is not correctly configured for this auction.",
                        ToBidRuleDetails(auction)));
            }
        }

        var minimumValidBid = BidRules.GetMinimumValidBid(auction);
        if (amount < minimumValidBid
            && (auction.BuyNowPrice is null || amount < auction.BuyNowPrice.Value))
        {
            return new BidValidationError(
                StatusCodes.Status400BadRequest,
                new ApiErrorResponse(
                    "bid_below_minimum",
                    "Bid amount does not satisfy the minimum bid.",
                    ToBidRuleDetails(auction)));
        }

        return null;
    }

    private static async Task<IResult>
        BuyNow(
        Guid id,
        BuyNowRequest request,
        BiddingDbContext db,
        TimeProvider timeProvider,
        IOptions<BidPlacementOptions> options,
        ILoggerFactory loggerFactory,
        IBuyerIdentityResolver identityResolver,
        ITenantIdentityAccessor tenantIdentityAccessor,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (!tenantIdentityAccessor.TryGetTenantId(httpContext.User, out var tenantId))
        {
            return TypedResults.Forbid();
        }

        var logger = loggerFactory.CreateLogger("BuyNow");
        var correlationId = ResolveCorrelationId(httpContext);
        httpContext.Response.Headers[CorrelationIdHeader] = correlationId;

        logger.LogInformation(
            "Buy Now attempt for auction {AuctionId}. CorrelationId: {CorrelationId}",
            id,
            correlationId);

        var bidderId = identityResolver.Resolve(httpContext, null);
        if (string.IsNullOrWhiteSpace(bidderId))
        {
            return TypedResults.BadRequest(
                new ApiErrorResponse("invalid_bidder", "BidderId is required."));
        }

        var maxRetries = Math.Max(0, options.Value.MaxConcurrencyRetries);
        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            await using var transaction = await db.Database
                .BeginTransactionAsync(cancellationToken);
            var auction = await db.Auctions.FirstOrDefaultAsync(
                a => a.Id == id && a.TenantId == tenantId,
                cancellationToken);
            if (auction is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return TypedResults.NotFound(
                    new ApiErrorResponse("auction_not_found", "Auction not found."));
            }

            var now = timeProvider.GetUtcNow();
            var validationError = ValidateBuyNow(auction, now);
            if (validationError is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                logger.LogInformation(
                    "Buy Now rejected after validation for auction {AuctionId}. SaleMode: {SaleMode}. Code: {Code}. Version: {AuctionVersion}. CorrelationId: {CorrelationId}",
                    auction.Id,
                    auction.SaleMode,
                    validationError.Code,
                    auction.Version,
                    correlationId);

                return TypedResults.Conflict(validationError.Error);
            }

            if (options.Value.ArtificialProcessingDelayMilliseconds > 0)
            {
                await Task.Delay(
                    options.Value.ArtificialProcessingDelayMilliseconds,
                    cancellationToken);
            }

            auction.FinalWinnerId = bidderId;
            auction.FinalPrice = auction.BuyNowPrice;
            auction.Status = AuctionStatus.Closed;
            auction.Version += 1;
            auction.UpdatedAtUtc = now;

            var purchasedMessage = OutboxMessageFactory.AuctionPurchased(
                auction,
                bidderId,
                correlationId,
                now);
            var closedMessage = OutboxMessageFactory.AuctionClosed(
                auction,
                correlationId,
                now.AddMilliseconds(1));
            db.OutboxMessages.AddRange(purchasedMessage, closedMessage);

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);

                logger.LogInformation(
                    "Completed Buy Now for auction {AuctionId}. SaleMode: {SaleMode}. Version advanced to {AuctionVersion}. PurchaseEventId: {PurchaseEventId}. ClosedEventId: {ClosedEventId}. CorrelationId: {CorrelationId}",
                    auction.Id,
                    auction.SaleMode,
                    auction.Version,
                    purchasedMessage.Id,
                    closedMessage.Id,
                    correlationId);

                var response = new BuyNowResponse(
                    auction.Id,
                    bidderId,
                    auction.FinalPrice!.Value,
                    auction.Version,
                    now,
                    correlationId);

                return TypedResults.Created($"/api/auctions/{auction.Id}", response);
            }
            catch (DbUpdateConcurrencyException)
            {
                await transaction.RollbackAsync(cancellationToken);
                db.ChangeTracker.Clear();

                logger.LogWarning(
                    "Buy Now concurrency conflict detected for auction {AuctionId} on attempt {Attempt} of {MaxAttempts}. CorrelationId: {CorrelationId}",
                    id,
                    attempt + 1,
                    maxRetries + 1,
                    correlationId);

                if (attempt == maxRetries)
                {
                    var currentAuction = await db.Auctions
                        .AsNoTracking()
                        .FirstOrDefaultAsync(
                            a => a.Id == id && a.TenantId == tenantId,
                            cancellationToken);
                    if (currentAuction is null)
                    {
                        return TypedResults.NotFound(
                            new ApiErrorResponse("auction_not_found", "Auction not found."));
                    }

                    var currentError = ValidateBuyNow(currentAuction, timeProvider.GetUtcNow());
                    return TypedResults.Conflict(
                        currentError?.Error
                        ?? new ApiErrorResponse(
                            "auction_concurrency_conflict",
                            "Auction state changed while the Buy Now purchase was being placed.",
                            ToBidRuleDetails(currentAuction)));
                }
            }
        }

        return TypedResults.Conflict(new ApiErrorResponse(
            "auction_concurrency_conflict",
            "Auction state changed while the Buy Now purchase was being placed."));
    }

    private static BuyNowValidationError? ValidateBuyNow(
        Auction auction,
        DateTimeOffset now)
    {
        if (auction.Status != AuctionStatus.Open)
        {
            return new BuyNowValidationError(
                "auction_not_open",
                new ApiErrorResponse(
                    "auction_not_open",
                    "Auction is not open for Buy Now.",
                    ToBidRuleDetails(auction)));
        }

        if (now < auction.StartTimeUtc)
        {
            return new BuyNowValidationError(
                "auction_not_started",
                new ApiErrorResponse(
                    "auction_not_started",
                    "Auction has not started yet.",
                    ToBidRuleDetails(auction)));
        }

        if (now >= auction.EndTimeUtc)
        {
            return new BuyNowValidationError(
                "auction_ended",
                new ApiErrorResponse(
                    "auction_ended",
                    "Auction has already ended.",
                    ToBidRuleDetails(auction)));
        }

        if (auction.SaleMode is not (SaleMode.BuyNowOnly or SaleMode.AuctionAndBuyNow))
        {
            return new BuyNowValidationError(
                "buy_now_not_available",
                new ApiErrorResponse(
                    "buy_now_not_available",
                    "Buy Now is not available for this auction.",
                    ToBidRuleDetails(auction)));
        }

        if (auction.BuyNowPrice is null || auction.BuyNowPrice <= 0)
        {
            return new BuyNowValidationError(
                "buy_now_not_available",
                new ApiErrorResponse(
                    "buy_now_not_available",
                    "Buy Now is not correctly configured for this auction.",
                    ToBidRuleDetails(auction)));
        }

        if (auction.FinalWinnerId is not null || auction.FinalPrice is not null)
        {
            return new BuyNowValidationError(
                "buy_now_not_available",
                new ApiErrorResponse(
                    "buy_now_not_available",
                    "Buy Now is no longer available for this auction.",
                    ToBidRuleDetails(auction)));
        }

        return null;
    }

    private static AuctionDetailResponse ToDetailResponse(Auction auction)
    {
        return new AuctionDetailResponse(
            auction.Id,
            auction.Title,
            auction.Description,
            auction.StartingPrice,
            auction.SaleMode.ToString(),
            auction.BuyNowPrice,
            auction.MinimumBidIncrement,
            auction.CurrentBidAmount,
            auction.CurrentBidderId,
            auction.FinalWinnerId,
            auction.FinalPrice,
            BidRules.GetMinimumValidBid(auction),
            auction.Status.ToString(),
            auction.StartTimeUtc,
            auction.EndTimeUtc,
            auction.CreatedAtUtc,
            auction.UpdatedAtUtc,
            auction.Version,
            auction.TenantId);
    }

    private static BidRuleErrorDetails ToBidRuleDetails(Auction auction)
    {
        return new BidRuleErrorDetails(
            auction.CurrentBidAmount,
            BidRules.GetMinimumValidBid(auction),
            auction.Version,
            auction.BuyNowPrice);
    }

    private static string ResolveCorrelationId(HttpContext httpContext)
    {
        var incoming = httpContext.Request.Headers[CorrelationIdHeader].FirstOrDefault();
        var normalized = incoming?.Trim();
        return string.IsNullOrWhiteSpace(normalized)
            || normalized.Length > 128
            || normalized.Any(char.IsControl)
            ? Guid.NewGuid().ToString("N")
            : normalized;
    }

    private sealed record BidValidationError(int StatusCode, ApiErrorResponse Error);
    private sealed record BuyNowValidationError(string Code, ApiErrorResponse Error);
}
