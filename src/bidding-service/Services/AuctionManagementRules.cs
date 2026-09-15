// <copyright file="AuctionManagementRules.cs" company="Distributed Bidding Auction Platform">
// Copyright (c) Distributed Bidding Auction Platform. Licensed under the MIT license.
// </copyright>
using bidding_service.Contracts;
using bidding_service.Domain;

namespace bidding_service.Services;

/// <summary>
/// Normalizes and validates management-provided auction configuration.
/// </summary>
public static class AuctionManagementRules
{
    /// <summary>
    /// Validates a sale-mode configuration and applies BuyNowOnly persistence compatibility.
    /// </summary>
    public static bool TryNormalize(
        string? saleModeText,
        decimal startingPrice,
        decimal minimumBidIncrement,
        decimal? buyNowPrice,
        out AuctionConfiguration configuration,
        out ApiErrorResponse? error)
    {
        configuration = default;
        error = null;

        if (!Enum.TryParse<SaleMode>(saleModeText, ignoreCase: true, out var saleMode)
            || !Enum.IsDefined(saleMode))
        {
            error = new ApiErrorResponse(
                "invalid_sale_mode_configuration",
                "SaleMode must be AuctionOnly, BuyNowOnly, or AuctionAndBuyNow.");
            return false;
        }

        if (minimumBidIncrement <= 0 || !HasMoneyPrecision(minimumBidIncrement))
        {
            error = new ApiErrorResponse(
                "invalid_bid_increment",
                "MinimumBidIncrement must be greater than zero and use at most two decimal places.");
            return false;
        }

        if (!HasMoneyPrecision(buyNowPrice))
        {
            error = new ApiErrorResponse(
                "invalid_buy_now_price",
                "BuyNowPrice must use at most two decimal places.");
            return false;
        }

        switch (saleMode)
        {
            case SaleMode.AuctionOnly:
                if (startingPrice <= 0 || !HasMoneyPrecision(startingPrice))
                {
                    error = new ApiErrorResponse(
                        "invalid_starting_price",
                        "StartingPrice must be greater than zero and use at most two decimal places.");
                    return false;
                }

                if (buyNowPrice is not null)
                {
                    error = new ApiErrorResponse(
                        "invalid_sale_mode_configuration",
                        "AuctionOnly auctions cannot have a BuyNowPrice.");
                    return false;
                }

                configuration = new AuctionConfiguration(saleMode, startingPrice, minimumBidIncrement, null);
                return true;

            case SaleMode.BuyNowOnly:
                if (buyNowPrice is null || buyNowPrice <= 0)
                {
                    error = new ApiErrorResponse(
                        "invalid_buy_now_price",
                        "BuyNowOnly auctions require a BuyNowPrice greater than zero.");
                    return false;
                }

                configuration = new AuctionConfiguration(
                    saleMode,
                    buyNowPrice.Value,
                    minimumBidIncrement,
                    buyNowPrice);
                return true;

            case SaleMode.AuctionAndBuyNow:
                if (startingPrice <= 0 || !HasMoneyPrecision(startingPrice))
                {
                    error = new ApiErrorResponse(
                        "invalid_starting_price",
                        "StartingPrice must be greater than zero and use at most two decimal places.");
                    return false;
                }

                if (buyNowPrice is null || buyNowPrice <= 0 || startingPrice >= buyNowPrice)
                {
                    error = new ApiErrorResponse(
                        "invalid_buy_now_price",
                        "AuctionAndBuyNow auctions require BuyNowPrice greater than StartingPrice.");
                    return false;
                }

                configuration = new AuctionConfiguration(saleMode, startingPrice, minimumBidIncrement, buyNowPrice);
                return true;

            default:
                error = new ApiErrorResponse(
                    "invalid_sale_mode_configuration",
                    "SaleMode is not supported.");
                return false;
        }
    }

    private static bool HasMoneyPrecision(decimal? value) =>
        value is null || decimal.Round(value.Value, 2) == value.Value;
}

/// <summary>
/// Represents normalized, persistence-ready auction pricing configuration.
/// </summary>
public readonly record struct AuctionConfiguration(
    SaleMode SaleMode,
    decimal StartingPrice,
    decimal MinimumBidIncrement,
    decimal? BuyNowPrice);
