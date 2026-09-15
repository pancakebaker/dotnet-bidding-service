// <copyright file="ClientCredentialProvisioningCommand.cs" company="Distributed Bidding Auction Platform">
// Copyright (c) Distributed Bidding Auction Platform. Licensed under the MIT license.
// </copyright>
using bidding_service.Data;
using Microsoft.EntityFrameworkCore;

namespace bidding_service.Services;

/// <summary>
/// Provides an internal command-line operator boundary for public credentials.
/// It is deliberately separate from the HTTP request pipeline.
/// </summary>
public static class ClientCredentialProvisioningCommand
{
    /// <summary>Returns whether the process was explicitly invoked for provisioning.</summary>
    public static bool IsCommand(string[] args) =>
        args.Length > 0 &&
        (args[0].Equals("provision", StringComparison.OrdinalIgnoreCase)
            || args[0].Equals("revoke", StringComparison.OrdinalIgnoreCase));

    /// <summary>Runs the requested operator operation and exits with a controlled result.</summary>
    public static async Task RunAsync(WebApplicationBuilder builder, string[] args)
    {
        try
        {
            var operation = Parse(args);
            if (operation.ShowHelp)
            {
                PrintHelp();
                return;
            }

            await using var app = builder.Build();
            await using var scope = app.Services.CreateAsyncScope();
            var provisioning = scope.ServiceProvider.GetRequiredService<IClientCredentialProvisioningService>();

            if (operation.Kind == OperationKind.Provision)
                await ProvisionAsync(provisioning, operation);
            else
                await RevokeAsync(provisioning, operation);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Client credential operation failed: {exception.Message}");
            Environment.ExitCode = 1;
        }
    }

    private static async Task ProvisionAsync(
        IClientCredentialProvisioningService provisioning,
        Operation operation)
    {
        var publicKeyPath = Require(operation.PublicKeyPath, "--public-key-path");
        if (!File.Exists(publicKeyPath) || Directory.Exists(publicKeyPath))
            throw new IOException($"Public key file does not exist: {publicKeyPath}");

        var credential = await provisioning.ProvisionAsync(
            Require(operation.ClientId, "--client-id"),
            Require(operation.KeyId, "--key-id"),
            await File.ReadAllTextAsync(publicKeyPath),
            ParseUtc(operation.ValidFromUtc, "--valid-from-utc") ?? DateTimeOffset.UtcNow,
            ParseUtc(operation.ExpiresAtUtc, "--expires-at-utc"));

        Console.WriteLine("Client credential provisioned.");
        Console.WriteLine($"CredentialId: {credential.Id}");
        Console.WriteLine($"KeyId: {credential.KeyId}");
        Console.WriteLine($"Fingerprint: {credential.PublicKeyFingerprint}");
        Console.WriteLine($"Status: {credential.Status}");
        Console.WriteLine($"ValidFromUtc: {credential.ValidFromUtc:O}");
        Console.WriteLine($"ExpiresAtUtc: {credential.ExpiresAtUtc:O}");
    }

    private static async Task RevokeAsync(
        IClientCredentialProvisioningService provisioning,
        Operation operation)
    {
        var keyId = Require(operation.KeyId, "--key-id");
        if (!await provisioning.RevokeAsync(keyId))
            throw new InvalidOperationException($"Credential key ID '{keyId}' was not found.");

        Console.WriteLine($"Client credential '{keyId}' revoked.");
    }

    private static DateTimeOffset? ParseUtc(string? value, string optionName)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (!DateTimeOffset.TryParse(value, out var parsed) || parsed.Offset != TimeSpan.Zero)
            throw new ArgumentException($"{optionName} must be an ISO-8601 UTC timestamp with offset +00:00.");

        return parsed;
    }

    private static string Require(string? value, string optionName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{optionName} is required.");

        return value;
    }

    private static Operation Parse(string[] args)
    {
        var operation = new Operation
        {
            Kind = args[0].Equals("provision", StringComparison.OrdinalIgnoreCase)
                ? OperationKind.Provision
                : OperationKind.Revoke
        };

        for (var index = 1; index < args.Length; index++)
        {
            var option = args[index];
            if (option.Equals("--help", StringComparison.OrdinalIgnoreCase))
            {
                operation.ShowHelp = true;
                continue;
            }

            if (option.Equals("--client-id", StringComparison.OrdinalIgnoreCase))
                operation.ClientId = Next(args, ref index, option);
            else if (option.Equals("--key-id", StringComparison.OrdinalIgnoreCase))
                operation.KeyId = Next(args, ref index, option);
            else if (option.Equals("--public-key-path", StringComparison.OrdinalIgnoreCase))
                operation.PublicKeyPath = Path.GetFullPath(Next(args, ref index, option));
            else if (option.Equals("--valid-from-utc", StringComparison.OrdinalIgnoreCase))
                operation.ValidFromUtc = Next(args, ref index, option);
            else if (option.Equals("--expires-at-utc", StringComparison.OrdinalIgnoreCase))
                operation.ExpiresAtUtc = Next(args, ref index, option);
            else
                throw new ArgumentException($"Unknown option '{option}'.");
        }

        return operation;
    }

    private static string Next(string[] args, ref int index, string option)
    {
        if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
            throw new ArgumentException($"{option} requires a value.");
        return args[index];
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Internal Bidding Service client-credential tool. It accepts public key material only.");
        Console.WriteLine("provision --client-id <id> --key-id <kid> --public-key-path <path> [--valid-from-utc <timestamp>] [--expires-at-utc <timestamp>]");
        Console.WriteLine("revoke --key-id <kid>");
        Console.WriteLine("The private key must remain with the client installation and is never read by this tool.");
    }

    private sealed class Operation
    {
        public OperationKind Kind { get; init; }
        public bool ShowHelp { get; set; }
        public string? ClientId { get; set; }
        public string? KeyId { get; set; }
        public string? PublicKeyPath { get; set; }
        public string? ValidFromUtc { get; set; }
        public string? ExpiresAtUtc { get; set; }
    }

    private enum OperationKind
    {
        Provision,
        Revoke
    }
}
