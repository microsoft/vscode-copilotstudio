// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.CopilotStudio.McsCore;

namespace Microsoft.CopilotStudio.Sync.Hosting;

internal static class WorkspaceSnapshotFiles
{
    public const char Separator = '/';

    public static void Write(IFileAccessorFactory files, DirectoryPath root, AgentWorkspaceSnapshot snapshot)
    {
        foreach (var folder in snapshot.Files.GroupBy(file => file.Key.Substring(0, file.Key.IndexOf(Separator)), StringComparer.Ordinal))
        {
            var workspace = files.Create(root.GetChildDirectoryPath(folder.Key));

            foreach (var file in folder)
            {
                workspace.Write(new AgentFilePath(file.Key.Substring(folder.Key.Length + 1)), file.Value);
            }
        }
    }

    public static AgentWorkspaceSnapshot Read(string folder, IEnumerable<KeyValuePair<string, IFileAccessor>> folders)
    {
        var files = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);

        foreach (var source in folders)
        {
            foreach (var path in source.Value.ListFiles().Select(file => file.ToString()).Where(path => !SyncStatePaths.IsSyncStatePath(path)).OrderBy(path => path, StringComparer.Ordinal))
            {
                files[source.Key + Separator + path] = source.Value.ReadBytes(new AgentFilePath(path));
            }
        }

        return new AgentWorkspaceSnapshot(folder, files);
    }

    public static IFileAccessor Open(AgentWorkspaceSnapshot snapshot, string folder)
    {
        var workspace = new InMemoryFileAccessor();
        var prefix = folder + Separator;

        foreach (var file in snapshot.Files.Where(file => file.Key.StartsWith(prefix, StringComparison.Ordinal)))
        {
            workspace.Write(new AgentFilePath(file.Key.Substring(prefix.Length)), file.Value);
        }

        return workspace;
    }
}
