// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.CopilotStudio.Sync;

/// <summary>Recognizes the paths that lie in a workspace's sync state ('.mcs') folder, which holds the cloud cache.</summary>
public static class SyncStatePaths
{
    /// <summary>Whether a '/'-separated path holds the sync state ('.mcs') folder as a segment at any depth, ignoring case.</summary>
    /// <param name="path">The path, or <see langword="null"/>.</param>
    public static bool IsSyncStatePath(string? path) => path is { Length: > 0 } && path.Split('/').Any(segment => string.Equals(segment, WorkspaceSynchronizer.HiddenRoot, StringComparison.OrdinalIgnoreCase));
}
