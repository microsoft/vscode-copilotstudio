// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Hidden per-child-agent link file (".agent.json").
//
// Child agents (AgentDialog) are never created locally; they only come from cloning.
// A child agent is projected onto disk as agents/<sanitized folder>/agent.mcs.yml, where
// the folder name is a (lossy, non-reversible) sanitization of the agent display name. To
// keep a durable link between that on-disk folder and the real cloud schema name - and to
// detect when a user renames the folder - clone/pull writes a hidden ".agent.json" beside
// every child agent's agent.mcs.yml recording { schemaName, folderName }.
//
// On synchronize, ValidateAll re-reads those link files:
//   - missing link file            -> the folder was hand-created (not cloned); hard-fail.
//   - folder name != link folder   -> the folder was renamed; hard-fail with the expected
//                                     (sanitized) folder name so the user can restore it.

using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CopilotStudio.McsCore;

namespace Microsoft.CopilotStudio.Sync;

internal static class ChildAgentLinkFile
{
    /// <summary>Hidden link file name written into every child agent folder.</summary>
    internal const string LinkFileName = ".agent.json";

    /// <summary>Top-level folder that holds child agent folders.</summary>
    private const string AgentsFolderPrefix = "agents/";

    /// <summary>Per-child-agent definition file name.</summary>
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

    /// <summary>
    /// Writes (or refreshes) the hidden link file beside a child agent's agent.mcs.yml.
    /// <paramref name="agentDefinitionPath"/> is the agent.mcs.yml path
    /// (e.g. <c>agents/Transfer Funds/agent.mcs.yml</c>); the link file lands next to it.
    /// </summary>
    internal static void WriteLink(IFileAccessor fileAccessor, AgentFilePath agentDefinitionPath, string schemaName)
    {
        if (!TryGetFolder(agentDefinitionPath.ToString(), out var folderName, out var linkPath))
        {
            // Not under agents/<folder>/ - nothing to link (defensive; the caller only
            // invokes this for child agents which always project under agents/).
            return;
        }

        var link = new ChildAgentLink { SchemaName = schemaName, FolderName = folderName };
        var json = JsonSerializer.Serialize(link, SerializerOptions);

        using var stream = fileAccessor.OpenWrite(linkPath);
        using var textWriter = new StreamWriter(stream, Encoding.UTF8);
        textWriter.Write(json);
    }

    /// <summary>
    /// Validates the hidden link file for every child agent folder on disk. Throws
    /// <see cref="InvalidOperationException"/> when a link file is missing/unreadable, or
    /// when the on-disk folder name no longer matches the sanitized name in the link file
    /// (i.e. the folder was renamed). No-op for workspaces without child agents.
    /// </summary>
    internal static void ValidateAll(IFileAccessor fileAccessor)
    {
        foreach (var agentDefinitionPath in fileAccessor.ListFiles("agents", AgentDefinitionFileName))
        {
            var pathValue = agentDefinitionPath.ToString();

            // ListFiles uses suffix/prefix matching in some accessors; restrict strictly to
            // agents/.../agent.mcs.yml so we only validate actual child agent definitions.
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
    /// Reads every present, well-formed child-agent link file on disk and returns its
    /// { schemaName, folderName }. Unlike <see cref="ValidateAll"/> this never throws -
    /// folders without a usable link are simply skipped - so callers can build the
    /// folder-&gt;real-schema map after validation has already enforced presence.
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
    /// From an <c>agents/.../&lt;folder&gt;/agent.mcs.yml</c> path, derives the immediate
    /// folder name (<c>&lt;folder&gt;</c>) and the sibling link file path
    /// (<c>agents/.../&lt;folder&gt;/.agent.json</c>). Returns false for paths not shaped
    /// like a child agent definition.
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
