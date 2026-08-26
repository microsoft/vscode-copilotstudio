// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.CopilotStudio.McsCore;

using System;

public interface IWorkspaceLease : IDisposable
{
    /// <summary>
    /// Gets the workspace root the lease holds.
    /// </summary>
    DirectoryPath Root { get; }

    /// <summary>
    /// Gets the accessor for the leased workspace.
    /// </summary>
    IFileAccessor Accessor { get; }
}
