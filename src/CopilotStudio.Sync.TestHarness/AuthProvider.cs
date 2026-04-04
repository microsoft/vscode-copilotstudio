// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.CopilotStudio.Sync;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

namespace Microsoft.CopilotStudio.Sync.TestHarness;

/// <summary>
/// ISyncAuthProvider backed by MSAL device code flow for interactive dev-loop use.
/// Reads COPILOT_TEST_TENANT_ID and COPILOT_TEST_CLIENT_ID from OS environment variables.
/// Tokens are cached to disk so they persist between process invocations.
/// </summary>
internal sealed class AuthProvider : ISyncAuthProvider
{
    private readonly IPublicClientApplication _app;
    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CopilotStudio.Sync.TestHarness");

    private AuthProvider(IPublicClientApplication app)
    {
        _app = app;
    }

    public static async Task<AuthProvider> CreateAsync()
    {
        var tenantId = GetRequiredEnvVar("COPILOT_TEST_TENANT_ID");
        var clientId = GetRequiredEnvVar("COPILOT_TEST_CLIENT_ID");

        var app = PublicClientApplicationBuilder
            .Create(clientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, tenantId)
            .Build();

        // Attach file-based token cache so tokens persist between invocations
        var storageProperties = new StorageCreationPropertiesBuilder("msal_token_cache.bin", CacheDir)
            .Build();
        var cacheHelper = await MsalCacheHelper.CreateAsync(storageProperties).ConfigureAwait(false);
        cacheHelper.RegisterCache(app.UserTokenCache);

        Console.Error.WriteLine("[auth] Using device code flow (token cache: " + CacheDir + ")");

        return new AuthProvider(app);
    }

    public async Task<string> AcquireTokenAsync(Uri audience, CancellationToken cancellationToken = default)
    {
        var scope = audience.ToString().TrimEnd('/') + "/.default";

        // Try silent first (cached tokens from disk)
        var accounts = await _app.GetAccountsAsync().ConfigureAwait(false);
        var account = accounts.FirstOrDefault();

        if (account != null)
        {
            try
            {
                var silent = await _app
                    .AcquireTokenSilent([scope], account)
                    .ExecuteAsync(cancellationToken)
                    .ConfigureAwait(false);
                return silent.AccessToken;
            }
            catch (MsalUiRequiredException)
            {
                // Token expired or scope not cached — fall through to device code
            }
        }

        var result = await _app
            .AcquireTokenWithDeviceCode([scope], deviceCodeResult =>
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine(deviceCodeResult.Message);
                Console.Error.WriteLine();
                return Task.CompletedTask;
            })
            .ExecuteAsync(cancellationToken)
            .ConfigureAwait(false);

        return result.AccessToken;
    }

    private static string GetRequiredEnvVar(string name)
    {
        return Environment.GetEnvironmentVariable(name)
            ?? throw new InvalidOperationException($"Required environment variable '{name}' is not set.");
    }
}
