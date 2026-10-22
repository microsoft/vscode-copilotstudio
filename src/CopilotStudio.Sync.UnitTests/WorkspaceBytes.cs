// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.CopilotStudio.McsCore;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

internal static class WorkspaceBytes
{
    public static byte[] Read(IFileAccessor workspace, AgentFilePath path)
    {
        using var stream = workspace.OpenRead(path);
        using var copy = new MemoryStream();

        stream.CopyTo(copy);

        return copy.ToArray();
    }

    public static byte[] Read(IFileAccessor workspace, string path) => Read(workspace, new AgentFilePath(path));
}
