// Copyright (C) Microsoft Corporation. All rights reserved.
// Ported from om/src/vscode/LanguageServers/PowerPlatformLS/Impl.PullAgent/File/IFileAccessorFactory.cs


namespace Microsoft.CopilotStudio.McsCore;

public interface IFileAccessorFactory
{
    /// <summary>
    /// Gets a value indicating whether workspaces are held in process memory rather than on disk.
    /// </summary>
    bool IsMemoryBacked { get; }

    IFileAccessor Create(DirectoryPath root);

    /// <summary>
    /// Releases a workspace and discards any content held for it.
    /// </summary>
    void Release(DirectoryPath root);
}
