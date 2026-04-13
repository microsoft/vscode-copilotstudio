// Copyright (C) Microsoft Corporation. All rights reserved.

using Azure.Core;
using Azure.Identity;
using Microsoft.CopilotStudio.Sync;

namespace Microsoft.CopilotStudio.Sync.TestHarness;

/// <summary>
/// ISyncAuthProvider backed by Azure.Identity interactive browser auth.
/// On first use, opens a browser for sign-in and persists an AuthenticationRecord
/// to disk. On subsequent runs, the record enables silent token acquisition from
/// the persistent cache — zero browser prompts.
///
/// Required environment variables:
///   COPILOT_TEST_TENANT_ID — AAD tenant ID (also used by Program.cs for AgentSyncInfo)
///   COPILOT_TEST_CLIENT_ID — public client app registration with Dataverse API permissions
/// </summary>
internal sealed class AuthProvider : ISyncAuthProvider
{
    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CopilotStudio.Sync.TestHarness");

    private static readonly string AuthRecordPath = Path.Combine(CacheDir, "auth-record.json");

    private readonly TokenCredential _credential;

    private AuthProvider(TokenCredential credential)
    {
        _credential = credential;
    }

    public static async Task<AuthProvider> CreateAsync()
    {
        var tenantId = GetRequiredEnvVar("COPILOT_TEST_TENANT_ID");
        var clientId = GetRequiredEnvVar("COPILOT_TEST_CLIENT_ID");

        Directory.CreateDirectory(CacheDir);

        var options = new InteractiveBrowserCredentialOptions
        {
            TenantId = tenantId,
            ClientId = clientId,
            TokenCachePersistenceOptions = new TokenCachePersistenceOptions
            {
                Name = "CopilotStudio.Sync.TestHarness",
            },
        };

        // Restore persisted AuthenticationRecord so the credential can find cached
        // tokens without opening the browser.
        if (File.Exists(AuthRecordPath))
        {
            using var stream = File.OpenRead(AuthRecordPath);
            options.AuthenticationRecord = await AuthenticationRecord.DeserializeAsync(stream).ConfigureAwait(false);
            Console.Error.WriteLine("[auth] Restored auth record — silent token acquisition enabled");
        }

        var credential = new InteractiveBrowserCredential(options);

        // If no persisted record, authenticate now (opens browser) and persist the
        // record for future runs.
        if (options.AuthenticationRecord == null)
        {
            Console.Error.WriteLine("[auth] No cached auth record — opening browser for initial sign-in...");
            var record = await credential.AuthenticateAsync().ConfigureAwait(false);
            using var stream = File.Create(AuthRecordPath);
            await record.SerializeAsync(stream).ConfigureAwait(false);
            Console.Error.WriteLine("[auth] Auth record saved to " + AuthRecordPath);
        }

        return new AuthProvider(credential);
    }

    public async Task<string> AcquireTokenAsync(Uri audience, CancellationToken cancellationToken = default)
    {
        var scope = audience.ToString().TrimEnd('/') + "/.default";
        var context = new TokenRequestContext([scope]);
        var token = await _credential.GetTokenAsync(context, cancellationToken).ConfigureAwait(false);
        return token.Token;
    }

    private static string GetRequiredEnvVar(string name)
    {
        return Environment.GetEnvironmentVariable(name)
            ?? throw new InvalidOperationException($"Required environment variable '{name}' is not set.");
    }
}
