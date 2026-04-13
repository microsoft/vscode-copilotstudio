// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.CommonLanguageServerProtocol.Framework;
using Microsoft.CopilotStudio.Sync;

namespace Microsoft.PowerPlatformLS.Impl.PullAgent.Auth;

/// <summary>
/// Bridges ISyncProgress for the shared sync library by forwarding messages
/// to the PullAgent's ILspLogger.
/// </summary>
internal sealed class LspSyncProgress : ISyncProgress
{
    private readonly ILspLogger _logger;

    public LspSyncProgress(ILspLogger logger)
    {
        _logger = logger;
    }

    public void Report(string message)
    {
        _logger.LogInformation("[sync] {0}", message);
    }
}
