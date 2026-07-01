// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Hidden per-child-agent link file (".agent.json"). Child agents come only from cloning;
// their on-disk folder is a lossy sanitization of the display name, so clone records the
// real cloud schema + folder here to correlate them on sync and detect folder renames.

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
        using var textWriter = new StreamWriter(stream, Encoding.UTF8);
        textWriter.Write(json);
    }

    /// <summary>
    /// Validates every child agent folder's link file: throws when a link is missing,
    /// unreadable, malformed, or its recorded folder no longer matches the on-disk folder
    /// (a rename). No-op for workspaces without child agents.
    /// </summary>
    internal static void ValidateAll(IFileAccessor fileAccessor)
    {
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

            if (!fileAccessor.Exists(linkPath))
            {
                throw new InvalidOperationException(
                    $"The child agent folder 'agents/{folderName}' is missing its '{LinkFileName}' link file. " +
                    "Child agents cannot be created locally - they must be cloned. Re-clone the agent to restore the link file.");
            }

            ChildAgentLink? link;
            try
            {
                link = ReadLink(fileAccessor, linkPath);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidOperationException(
                    $"The child agent link file 'agents/{folderName}/{LinkFileName}' could not be read or parsed: {ex.Message}. " +
                    "Re-clone the agent to restore the link file.");
            }

            if (link == null || string.IsNullOrEmpty(link.FolderName))
            {
                throw new InvalidOperationException(
                    $"The child agent link file 'agents/{folderName}/{LinkFileName}' is malformed (missing 'folderName'). " +
                    "Re-clone the agent to restore the link file.");
            }

            if (!string.Equals(folderName, link.FolderName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The child agent folder name '{folderName}' does not match the expected name '{link.FolderName}'. " +
                    $"Rename the folder back to '{link.FolderName}' before syncing.");
            }
        }
    }

    private static ChildAgentLink? ReadLink(IFileAccessor fileAccessor, AgentFilePath linkPath)
    {
        using var stream = fileAccessor.OpenRead(linkPath);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var json = reader.ReadToEnd();
        return JsonSerializer.Deserialize<ChildAgentLink>(json, SerializerOptions);
    }

    /// <summary>
    /// Returns every present, well-formed link. Never throws (skips unusable folders);
    /// call after <see cref="ValidateAll"/> when building the folder-&gt;schema map.
    /// </summary>
    internal static IReadOnlyList<ChildAgentLink> ReadAll(IFileAccessor fileAccessor)
    {
        var links = new List<ChildAgentLink>();

        foreach (var agentDefinitionPath in fileAccessor.ListFiles("agents", AgentDefinitionFileName))
        {
            var pathValue = agentDefinitionPath.ToString();

            if (!pathValue.StartsWith(AgentsFolderPrefix, StringComparison.Ordinal)
                || !string.Equals(agentDefinitionPath.FileName, AgentDefinitionFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!TryGetFolder(pathValue, out _, out var linkPath) || !fileAccessor.Exists(linkPath))
            {
                continue;
            }

            ChildAgentLink? link;
            try
            {
                link = ReadLink(fileAccessor, linkPath);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                continue;
            }

            if (link != null && !string.IsNullOrEmpty(link.SchemaName) && !string.IsNullOrEmpty(link.FolderName))
            {
                links.Add(link);
            }
        }

        return links;
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
