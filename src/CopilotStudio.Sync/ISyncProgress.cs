// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.CopilotStudio.Sync;

/// <summary>
/// Reports progress and diagnostic messages during sync operations.
/// Each host (pac, test CLI, VS Code extension) implements this for its output mechanism.
/// </summary>
public interface ISyncProgress
{
    /// <summary>
    /// Reports a progress or diagnostic message to the host.
    /// </summary>
    /// <param name="message">The message to report.</param>
    void Report(string message);
}
