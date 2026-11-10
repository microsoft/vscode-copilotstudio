// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.CopilotStudio.Sync.Hosting;

internal sealed class SilentSyncProgress : ISyncProgress
{
    public static SilentSyncProgress Instance { get; } = new();

    public void Report(string message)
    {
    }
}
