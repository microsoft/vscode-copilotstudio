// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.CopilotStudio.McsCore;

namespace Microsoft.CopilotStudio.Sync;

internal static class WorkspaceFolders
{
    public static void Move(IFileAccessor workspace, string sourceFolder, string targetFolder)
    {
        var prefix = sourceFolder + "/";
        var sources = workspace.ListFiles(sourceFolder, "*").Where(path => path.ToString().StartsWith(prefix, StringComparison.Ordinal)).ToList();

        foreach (var source in sources)
        {
            using var reader = workspace.OpenRead(source);
            using var writer = workspace.OpenWrite(new AgentFilePath(targetFolder + "/" + source.ToString().Substring(prefix.Length)));

            reader.CopyTo(writer);
        }

        foreach (var source in sources)
        {
            workspace.Delete(source);
        }

        Remove(workspace, sourceFolder);
    }

    public static void Remove(IFileAccessor workspace, string folder) => workspace.DeleteDirectory(new AgentFilePath(folder));
}