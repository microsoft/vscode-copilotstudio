// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.CopilotStudio.Sync;

/// <summary>
/// Provides authentication tokens for sync operations against Copilot Studio APIs.
/// Each host (pac, test CLI, VS Code extension) implements this for its own auth mechanism.
/// </summary>
public interface ISyncAuthProvider
{
    /// <summary>
    /// Acquires a bearer token for the specified resource audience.
    /// </summary>
    /// <param name="audience">The token audience URI (e.g., the Azure AD app ID for the target API).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A bearer token string for the requested audience.</returns>
    Task<string> AcquireTokenAsync(Uri audience, CancellationToken cancellationToken = default);
}
