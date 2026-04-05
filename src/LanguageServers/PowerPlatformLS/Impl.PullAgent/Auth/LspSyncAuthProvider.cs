// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.CopilotStudio.Sync;

namespace Microsoft.PowerPlatformLS.Impl.PullAgent.Auth;

/// <summary>
/// Bridges ISyncAuthProvider for the shared sync library by returning pre-acquired
/// tokens from the PullAgent's ITokenProvider. The VS Code client acquires tokens
/// and passes them in each request; this adapter maps the audience URI the shared
/// library requests to the correct pre-acquired token.
///
/// Audience mapping:
///   api:// URIs (Island control plane app IDs) → CopilotStudio token
///   https:// URIs (Dataverse org URLs)          → Dataverse token
/// </summary>
internal sealed class LspSyncAuthProvider : ISyncAuthProvider
{
    private readonly ITokenProvider _tokenProvider;

    public LspSyncAuthProvider(ITokenProvider tokenProvider)
    {
        _tokenProvider = tokenProvider;
    }

    public Task<string> AcquireTokenAsync(Uri audience, CancellationToken cancellationToken = default)
    {
        // Island control plane audiences use the "api" scheme (e.g., api://96ff4394-...).
        // Dataverse audiences use "https" (e.g., https://org.crm.dynamics.com).
        var token = string.Equals(audience.Scheme, "api", StringComparison.OrdinalIgnoreCase)
            ? _tokenProvider.GetCopilotStudioToken()
            : _tokenProvider.GetDataverseToken();

        return Task.FromResult(token);
    }
}
