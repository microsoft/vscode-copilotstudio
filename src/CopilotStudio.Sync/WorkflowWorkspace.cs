// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Text.RegularExpressions;

namespace Microsoft.CopilotStudio.Sync;

/// <summary>One workflow folder found beneath a workflow workspace root.</summary>
public sealed record WorkflowFolder(Guid WorkflowId, string FolderName, string FolderPath, string DefinitionPath, string MetadataPath);

/// <summary>Describes how workflows are laid out beneath a workspace root.</summary>
public static class WorkflowWorkspace
{
    /// <summary>The folder every workflow lives beneath, relative to a workspace root.</summary>
    public const string WorkflowsFolderName = "workflows";

    /// <summary>The file holding a workflow's definition.</summary>
    public const string DefinitionFileName = "workflow.json";

    /// <summary>The file holding a workflow's Dataverse metadata.</summary>
    public const string MetadataFileName = "metadata.yml";

    private static readonly Regex TrailingIdentifier = new("([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})$", RegexOptions.CultureInvariant);

    private static readonly string[] AgentProjectFileNames = { "settings.mcs.yml", "agent.mcs.yml", "collection.mcs.yml" };

    private static readonly char[] InvalidFolderNameCharacters =
        Path.GetInvalidFileNameChars().Concat("\\/:*?\"<>|").Distinct().ToArray();

    /// <summary>The workflows folder beneath a workspace root.</summary>
    public static string WorkflowsDirectory(string workspaceRoot) => Path.Combine(workspaceRoot, WorkflowsFolderName);

    /// <summary>The folder name a workflow of this name and identifier occupies.</summary>
    public static string FolderNameFor(string? name, Guid workflowId) =>
        $"{new string((name ?? string.Empty).Where(Portable).ToArray()).TrimEnd('.', ' ')}-{workflowId}";

    /// <summary>
    /// Whether a character means the same thing in a folder name on every platform. The invalid set
    /// a host reports is the host's own, so filtering by it alone would name one workflow's folder
    /// differently on Windows and on Linux.
    /// </summary>
    private static bool Portable(char character) =>
        !char.IsControl(character) && !char.IsWhiteSpace(character) && !InvalidFolderNameCharacters.Contains(character);

    /// <summary>The relative folder a workflow of this name and identifier occupies.</summary>
    public static string RelativeFolderFor(string? name, Guid workflowId) => $"{WorkflowsFolderName}/{FolderNameFor(name, workflowId)}";

    /// <summary>The identifier a workflow folder name ends with.</summary>
    public static Guid? WorkflowIdOf(string? folderName) =>
        TrailingIdentifier.Match(folderName ?? string.Empty) is { Success: true } match && Guid.TryParse(match.Value, out var workflowId) ? workflowId : null;

    /// <summary>Every workflow folder beneath a workspace root, in name order.</summary>
    public static IReadOnlyList<WorkflowFolder> FindAll(string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot) || !Directory.Exists(WorkflowsDirectory(workspaceRoot)))
        {
            return Array.Empty<WorkflowFolder>();
        }

        return Directory.EnumerateDirectories(WorkflowsDirectory(workspaceRoot))
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .Select(Describe)
            .Where(folder => folder is not null)
            .Select(folder => folder!)
            .ToArray();
    }

    /// <summary>Whether a directory holds an agent project of its own.</summary>
    public static bool HasAgentProject(string? directory) =>
        !string.IsNullOrWhiteSpace(directory)
        && AgentProjectFileNames.Any(fileName => File.Exists(Path.Combine(directory!, fileName)));

    /// <summary>Whether a directory holds workflows and no agent project of its own.</summary>
    public static bool IsStandaloneWorkspace(string? directory) =>
        !string.IsNullOrWhiteSpace(directory)
        && !HasAgentProject(directory)
        && FindAll(directory!).Count > 0;

    /// <summary>Whether a folder beneath the workspace names a workflow but is missing one of its files.</summary>
    /// <param name="workspaceRoot">The workspace root to search.</param>
    /// <returns><see langword="true"/> when a workflow folder is half written.</returns>
    public static bool HasIncompleteWorkflow(string workspaceRoot) =>
        !string.IsNullOrWhiteSpace(workspaceRoot)
        && Directory.Exists(WorkflowsDirectory(workspaceRoot))
        && Directory.EnumerateDirectories(WorkflowsDirectory(workspaceRoot))
            .Any(folderPath => WorkflowIdOf(new DirectoryInfo(folderPath).Name) is not null && Describe(folderPath) is null);

    private static WorkflowFolder? Describe(string folderPath)
    {
        var folderName = new DirectoryInfo(folderPath).Name;

        return WorkflowIdOf(folderName) is { } workflowId
            && File.Exists(Path.Combine(folderPath, DefinitionFileName))
            && File.Exists(Path.Combine(folderPath, MetadataFileName))
            ? new WorkflowFolder(workflowId, folderName, folderPath, Path.Combine(folderPath, DefinitionFileName), Path.Combine(folderPath, MetadataFileName))
            : null;
    }
}
