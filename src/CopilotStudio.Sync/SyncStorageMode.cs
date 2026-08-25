// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.CopilotStudio.Sync;

/// <summary>
/// Selects where the shared sync library holds agent workspaces.
/// </summary>
public enum SyncStorageMode
{
    /// <summary>
    /// Workspaces live on the file system at the supplied <see cref="McsCore.DirectoryPath"/>.
    /// </summary>
    Physical = 0,

    /// <summary>
    /// Workspaces live in process memory.
    /// </summary>
    InMemory = 1,
}
