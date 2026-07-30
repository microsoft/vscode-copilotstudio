// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Text;
using Microsoft.CopilotStudio.McsCore;

namespace Microsoft.CopilotStudio.Sync;

internal static class ChildAgentLinkFile
{
    internal const string LinkFileName = ChildAgentLink.LinkFileName;

    /// <summary>An <c>agents/&lt;FolderName&gt;/</c> child-agent folder and its parsed link (null when missing/malformed).</summary>
    internal readonly record struct ChildAgentFolder(string FolderName, SchemaLinkData? Link);

    /// <summary>Writes the hidden link file beside a child agent's agent.mcs.yml.</summary>
    internal static void WriteLink(IFileAccessor fileAccessor, AgentFilePath agentDefinitionPath, string schemaName)
    {
        if (!TryGetFolder(agentDefinitionPath.ToString(), out var folderName, out var linkPath))
        {
            return;
        }

        var link = new SchemaLinkData { SchemaName = schemaName, FolderName = folderName };
        var json = SchemaLink.Serialize(link);

        using var stream = fileAccessor.OpenWrite(linkPath);
        using var textWriter = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        textWriter.Write(json);
    }

    /// <summary>
    /// Deletes the hidden link file beside a child agent's agent.mcs.yml. No-op when the path
    /// isn't shaped like a child agent or the link file is absent. Call when the child agent is
    /// removed so the sidecar doesn't outlive it.
    /// </summary>
    internal static void DeleteLink(IFileAccessor fileAccessor, AgentFilePath agentDefinitionPath)
    {
        if (TryGetFolder(agentDefinitionPath.ToString(), out _, out var linkPath))
        {
            fileAccessor.Delete(linkPath);
        }
    }

    /// <summary>
    /// Enumerates every <c>agents/.../&lt;folder&gt;/agent.mcs.yml</c> on disk with its parsed
    /// <c>.agent.json</c> link (<see cref="ChildAgentFolder.Link"/> is null when the link file
    /// is missing or malformed). No-op for workspaces without child agents.
    /// </summary>
    internal static IReadOnlyList<ChildAgentFolder> ListFolders(IFileAccessor fileAccessor)
    {
        var folders = new List<ChildAgentFolder>();

        foreach (var agentDefinitionPath in fileAccessor.ListFiles(ChildAgentLink.AgentsFolderName, ChildAgentLink.AgentDefinitionFileName))
        {
            var pathValue = agentDefinitionPath.ToString();

            // ListFiles matches by suffix in some accessors; restrict to real agents/.../agent.mcs.yml.
            if (!pathValue.StartsWith(ChildAgentLink.AgentsFolderPrefix, StringComparison.Ordinal)
                || !string.Equals(agentDefinitionPath.FileName, ChildAgentLink.AgentDefinitionFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!TryGetFolder(pathValue, out var folderName, out var linkPath))
            {
                continue;
            }

            var parsed = SchemaLink.TryRead(fileAccessor, linkPath);
            var link = parsed != null && !string.IsNullOrEmpty(parsed.SchemaName) && !string.IsNullOrEmpty(parsed.FolderName) ? parsed : null;

            folders.Add(new ChildAgentFolder(folderName, link));
        }

        return folders;
    }

    /// <summary>
    /// Splits <c>agents/.../&lt;folder&gt;/agent.mcs.yml</c> into the immediate folder name and
    /// its sibling <c>.agent.json</c> path. False for paths not shaped like a child agent.
    /// </summary>
    private static bool TryGetFolder(string agentDefinitionPath, out string folderName, out AgentFilePath linkPath)
    {
        folderName = string.Empty;
        linkPath = default;

        var lastSlash = agentDefinitionPath.LastIndexOf('/');
        if (lastSlash <= 0)
        {
            return false;
        }

        var directory = agentDefinitionPath.Substring(0, lastSlash);
        var prevSlash = directory.LastIndexOf('/');
        folderName = prevSlash >= 0 ? directory.Substring(prevSlash + 1) : directory;

        if (string.IsNullOrEmpty(folderName))
        {
            return false;
        }

        linkPath = new AgentFilePath(directory + "/" + LinkFileName);
        return true;
    }
}
