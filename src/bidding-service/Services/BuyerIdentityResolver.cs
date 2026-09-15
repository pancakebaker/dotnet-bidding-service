// <copyright file="BuyerIdentityResolver.cs" company="Distributed Bidding Auction Platform">
// Copyright (c) Distributed Bidding Auction Platform. Licensed under the MIT license.
// </copyright>
using System.Security.Claims;

namespace bidding_service.Services;

/// <summary>
/// Resolves the identity used by bidder and purchase commands.
/// </summary>
public interface IBuyerIdentityResolver
{
    /// <summary>
    /// Resolves an authenticated identity or the temporary demo request identity.
    /// </summary>
    /// <param name="httpContext">The current HTTP context.</param>
    /// <param name="requestedIdentity">The legacy development-only fallback identity.</param>
    /// <returns>The resolved identity, or <see langword="null"/> when unavailable.</returns>
    string? Resolve(HttpContext httpContext, string? requestedIdentity);
}

/// <summary>
/// Uses an authenticated principal when available and retains the request
/// identity only as a legacy unauthenticated development fallback. Protected
/// bid and Buy Now endpoints always pass a null fallback.
/// </summary>
public sealed class BuyerIdentityResolver : IBuyerIdentityResolver
{
    /// <inheritdoc />
    public string? Resolve(HttpContext httpContext, string? requestedIdentity)
    {
        if (httpContext.User.Identity?.IsAuthenticated == true)
        {
            return httpContext.User.FindFirstValue("sub")
                ?? httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        }

        return requestedIdentity?.Trim();
    }
}
