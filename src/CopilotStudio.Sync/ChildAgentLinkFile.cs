// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Hidden per-child-agent link file (".agent.json"). Child agents come only from cloning;
// their on-disk folder is a lossy sanitization of the display name, so clone records the
// real cloud schema + folder here to correlate them on sync and detect folder renames.
// A missing/malformed link is not fatal: WorkspaceSynchronizer self-heals it from the cloud
// cache (see ResolveChildAgentSchemas), so workspaces cloned before this file existed keep working.

using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CopilotStudio.McsCore;

namespace Microsoft.CopilotStudio.Sync;

internal static class ChildAgentLinkFile
{
    internal const string LinkFileName = ".agent.json";
    private const string AgentsFolderPrefix = "agents/";
    private const string AgentDefinitionFileName = "agent.mcs.yml";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    internal sealed class ChildAgentLink
    {
        [JsonPropertyName("schemaName")]
        public string SchemaName { get; set; } = string.Empty;

        [JsonPropertyName("folderName")]
        public string FolderName { get; set; } = string.Empty;
    }

    /// <summary>An <c>agents/&lt;FolderName&gt;/</c> child-agent folder and its parsed link (null when missing/malformed).</summary>
    internal readonly record struct ChildAgentFolder(string FolderName, ChildAgentLink? Link);

    /// <summary>Writes the hidden link file beside a child agent's agent.mcs.yml.</summary>
    internal static void WriteLink(IFileAccessor fileAccessor, AgentFilePath agentDefinitionPath, string schemaName)
    {
        if (!TryGetFolder(agentDefinitionPath.ToString(), out var folderName, out var linkPath))
        {
            return;
        }

        var link = new ChildAgentLink { SchemaName = schemaName, FolderName = folderName };
        var json = JsonSerializer.Serialize(link, SerializerOptions);

        using var stream = fileAccessor.OpenWrite(linkPath);
        using var textWriter = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        textWriter.Write(json);
    }

    /// <summary>
    /// Enumerates every <c>agents/.../&lt;folder&gt;/agent.mcs.yml</c> on disk with its parsed
    /// <c>.agent.json</c> link (<see cref="ChildAgentFolder.Link"/> is null when the link file
    /// is missing or malformed). No-op for workspaces without child agents.
    /// </summary>
    internal static IReadOnlyList<ChildAgentFolder> ListFolders(IFileAccessor fileAccessor)
    {
        var folders = new List<ChildAgentFolder>();

        foreach (var agentDefinitionPath in fileAccessor.ListFiles("agents", AgentDefinitionFileName))
        {
            var pathValue = agentDefinitionPath.ToString();

            // ListFiles matches by suffix in some accessors; restrict to real agents/.../agent.mcs.yml.
            if (!pathValue.StartsWith(AgentsFolderPrefix, StringComparison.Ordinal)
                || !string.Equals(agentDefinitionPath.FileName, AgentDefinitionFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!TryGetFolder(pathValue, out var folderName, out var linkPath))
            {
                continue;
            }

            ChildAgentLink? link = null;
            if (fileAccessor.Exists(linkPath))
            {
                try
                {
                    var parsed = ReadLink(fileAccessor, linkPath);
                    if (parsed != null && !string.IsNullOrEmpty(parsed.SchemaName) && !string.IsNullOrEmpty(parsed.FolderName))
                    {
                        link = parsed;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    link = null;
                }
            }

            folders.Add(new ChildAgentFolder(folderName, link));
        }

        return folders;
    }

    private static ChildAgentLink? ReadLink(IFileAccessor fileAccessor, AgentFilePath linkPath)
    {
        using var stream = fileAccessor.OpenRead(linkPath);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var json = reader.ReadToEnd();
        return JsonSerializer.Deserialize<ChildAgentLink>(json, SerializerOptions);
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
