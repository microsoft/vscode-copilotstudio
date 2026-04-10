// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.CopilotStudio.Sync;

namespace Microsoft.CopilotStudio.Sync.TestHarness;

internal sealed class ConsoleSyncProgress : ISyncProgress
{
    public void Report(string message)
    {
        Console.WriteLine($"[sync] {message}");
    }
}
