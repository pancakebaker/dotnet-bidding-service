// <copyright file="ClientAssertionAdmissionOptionsValidator.cs" company="Distributed Bidding Auction Platform">
// Copyright (c) Distributed Bidding Auction Platform. Licensed under the MIT license.
// </copyright>
using Microsoft.Extensions.Options;

namespace bidding_service.Options;

/// <summary>Enforces the production policy for client assertion admission.</summary>
public sealed class ClientAssertionAdmissionOptionsValidator(bool isProduction)
    : IValidateOptions<ClientAssertionAdmissionOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, ClientAssertionAdmissionOptions options) =>
        isProduction && !options.Enabled
            ? ValidateOptionsResult.Fail(
                "Client assertion admission must be enabled in Production. "
                + "Configure ClientAssertionAdmission:Enabled=true before serving production traffic.")
            : ValidateOptionsResult.Success;
}
