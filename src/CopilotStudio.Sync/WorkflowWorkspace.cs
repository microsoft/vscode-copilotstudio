// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Text;
using Microsoft.CopilotStudio.McsCore;

namespace Microsoft.CopilotStudio.Sync;

/// <summary>One workflow folder found beneath a workflow workspace root.</summary>
public sealed record WorkflowFolder(Guid WorkflowId, string FolderName, string FolderPath, string DefinitionPath, string MetadataPath);

/// <summary>One workflow found beneath a workspace's workflows folder, addressed through a file accessor.</summary>
public sealed record WorkflowFile(Guid WorkflowId, string FolderName, AgentFilePath DefinitionPath, AgentFilePath MetadataPath);

/// <summary>What a directory offers a standalone workflow project that would be written into it.</summary>
public enum WorkflowProjectHost
{
    /// <summary>The directory is absent, empty, or already holds this workflow with nothing unpushed.</summary>
    Free = 0,

    /// <summary>The directory holds an agent, another workflow, or content the clone did not write.</summary>
    Occupied = 1,

    /// <summary>The directory holds this workflow carrying an edit the service has not seen.</summary>
    UnpushedChanges = 2,
}

/// <summary>Describes how workflows are laid out beneath a workspace root.</summary>
public static class WorkflowWorkspace
{
    /// <summary>The folder every workflow lives beneath, relative to a workspace root.</summary>
    public const string WorkflowsFolderName = WorkflowLayout.WorkflowsFolderName;

    /// <summary>The file holding a workflow's definition.</summary>
    public const string DefinitionFileName = WorkflowLayout.DefinitionFileName;

    /// <summary>The file holding a workflow's Dataverse metadata.</summary>
    public const string MetadataFileName = WorkflowLayout.MetadataFileName;

    /// <summary>The file declaring a workspace to be a standalone workflow project.</summary>
    public const string ProjectFileName = "workflow.sync.yaml";

    /// <summary>The current standalone workflow project schema version.</summary>
    public const int CurrentProjectVersion = 1;

    private const int IdentifierLength = 36;

    private static readonly AgentFilePath ProjectFilePath = new AgentFilePath(ProjectFileName);

    private static readonly AgentFilePath LayoutMarkerPath = new AgentFilePath(AgentClassifier.WorkspaceLayoutMarkerFileName);

    /// <summary>The workflows folder beneath a workspace root.</summary>
    public static string WorkflowsDirectory(string workspaceRoot) => Path.Combine(workspaceRoot, WorkflowsFolderName);

    /// <summary>The folder name a workflow of this name and identifier occupies.</summary>
    public static string FolderNameFor(string? name, Guid workflowId) => WorkspaceFolderNames.ForWorkflow(name, workflowId);

    /// <summary>The folder name a standalone workflow project of this name occupies beneath a root.</summary>
    /// <param name="name">The workflow name the folder is derived from.</param>
    /// <returns>The portable folder name, empty when the name carries no character a folder may hold.</returns>
    public static string ProjectFolderNameFor(string? name) => WorkspaceFolderNames.ForWorkflow(name);

    /// <summary>The relative folder a workflow of this name and identifier occupies.</summary>
    public static string RelativeFolderFor(string? name, Guid workflowId) => $"{WorkflowsFolderName}/{FolderNameFor(name, workflowId)}";

    /// <summary>The identifier a workflow folder name ends with.</summary>
    public static Guid? WorkflowIdOf(string? folderName) =>
        folderName is { Length: >= IdentifierLength } name && Guid.TryParseExact(name.Substring(name.Length - IdentifierLength), "D", out var workflowId) ? workflowId : null;

    /// <summary>Writes the workspace layout marker unless it is already current.</summary>
    /// <param name="workspaceRoot">The workspace root to mark.</param>
    /// <returns><see langword="true"/> when the marker changed.</returns>
    public static bool EnsureLayoutMarker(string workspaceRoot) => EnsureLayoutMarker(OpenWorkspace(workspaceRoot));

    /// <summary>Writes the workspace layout marker unless it is already current.</summary>
    /// <param name="fileAccessor">The workspace to mark.</param>
    /// <returns><see langword="true"/> when the marker changed.</returns>
    public static bool EnsureLayoutMarker(IFileAccessor fileAccessor) =>
        ConnectionReferenceStore.WriteIfChanged(fileAccessor, LayoutMarkerPath, AgentClassifier.BuildWorkspaceLayoutMarker(), Encoding.UTF8);

    /// <summary>Opens a workspace root for file access.</summary>
    /// <param name="workspaceRoot">The workspace root directory.</param>
    /// <returns>An accessor rooted at the workspace.</returns>
    public static IFileAccessor OpenWorkspace(string workspaceRoot) =>
        new FileAccessorFactory().Create(new DirectoryPath(workspaceRoot.Replace('\\', '/')));

    /// <summary>Every workflow folder beneath a workspace root, in name order.</summary>
    /// <param name="workspaceRoot">The workspace root to search.</param>
    /// <returns>The workflows the root holds, each with its absolute paths.</returns>
    public static IReadOnlyList<WorkflowFolder> FindAll(string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            return Array.Empty<WorkflowFolder>();
        }

        var workflowsDirectory = WorkflowsDirectory(workspaceRoot);

        return FindAll(OpenWorkspace(workspaceRoot))
            .Select(file => new WorkflowFolder(
                file.WorkflowId,
                file.FolderName,
                Path.Combine(workflowsDirectory, file.FolderName),
                Path.Combine(workflowsDirectory, file.FolderName, DefinitionFileName),
                Path.Combine(workflowsDirectory, file.FolderName, MetadataFileName)))
            .ToArray();
    }

    /// <summary>Every workflow beneath a workspace's workflows folder, in name order.</summary>
    /// <param name="fileAccessor">The workspace to search.</param>
    /// <returns>The workflows the workspace holds, each carrying both of its files.</returns>
    public static IReadOnlyList<WorkflowFile> FindAll(IFileAccessor fileAccessor)
    {
        if (fileAccessor is null)
        {
            return Array.Empty<WorkflowFile>();
        }

        var found = new List<WorkflowFile>();

        foreach (var definitionPath in fileAccessor.ListFiles(WorkflowsFolderName, DefinitionFileName))
        {
            if (FolderNameOf(definitionPath.ToString()) is not { } folderName
                || !string.Equals(definitionPath.ToString(), RelativePath(folderName, DefinitionFileName), StringComparison.Ordinal)
                || WorkflowIdOf(folderName) is not { } workflowId)
            {
                continue;
            }

            var metadataPath = new AgentFilePath(RelativePath(folderName, MetadataFileName));

            if (fileAccessor.Exists(metadataPath))
            {
                found.Add(new WorkflowFile(workflowId, folderName, definitionPath, metadataPath));
            }
        }

        return found.OrderBy(file => file.FolderName, StringComparer.Ordinal).ToArray();
    }

    /// <summary>The only workflow a workspace holds, refusing a workspace that holds none or several.</summary>
    /// <param name="fileAccessor">The workspace to search.</param>
    /// <param name="subject">What to name as missing when the workspace holds no workflow.</param>
    /// <returns>The only workflow found.</returns>
    public static WorkflowFile RequireSingle(IFileAccessor fileAccessor, string subject)
    {
        var found = FindAll(fileAccessor);

        if (found.Count == 1)
        {
            return found[0];
        }

        throw found.Count == 0
            ? StandaloneWorkflowException.NotFound(subject)
            : StandaloneWorkflowException.Ambiguous(found.Select(file => file.FolderName));
    }

    /// <summary>Whether a workspace declares itself a standalone workflow project.</summary>
    /// <param name="fileAccessor">The workspace to test.</param>
    public static bool HasProjectFile(IFileAccessor fileAccessor) => fileAccessor is not null && fileAccessor.Exists(ProjectFilePath);

    /// <summary>
    /// What a directory offers one workflow's standalone project. Content the clone did not write
    /// stays untouched, a directory already holding another workflow is refused because every later
    /// operation on a standalone project requires the project to name exactly one workflow, and a
    /// project carrying an edit the service has not seen is refused because a clone overwrites it.
    /// </summary>
    /// <param name="projectRoot">The candidate project directory.</param>
    /// <param name="workflowId">The workflow the project would hold.</param>
    /// <returns>What the directory offers.</returns>
    public static WorkflowProjectHost InspectProject(string projectRoot, Guid workflowId)
    {
        if (string.IsNullOrWhiteSpace(projectRoot) || !Directory.Exists(projectRoot))
        {
            return WorkflowProjectHost.Free;
        }

        if (!Directory.EnumerateFileSystemEntries(projectRoot).Any())
        {
            return WorkflowProjectHost.Free;
        }

        var workspace = OpenWorkspace(projectRoot);
        var found = FindAll(workspace);

        if (HasAgentProject(workspace) || found.Count != 1 || found[0].WorkflowId != workflowId)
        {
            return WorkflowProjectHost.Occupied;
        }

        return StandaloneWorkflowCache.HasLocalChange(workspace, found[0]) ? WorkflowProjectHost.UnpushedChanges : WorkflowProjectHost.Free;
    }

    /// <summary>Whether a directory is free to hold one workflow's standalone project.</summary>
    /// <param name="projectRoot">The candidate project directory.</param>
    /// <param name="workflowId">The workflow the project would hold.</param>
    /// <returns><see langword="true"/> when the directory is absent, empty, or already holds this workflow with nothing unpushed.</returns>
    public static bool CanHostProject(string projectRoot, Guid workflowId) => InspectProject(projectRoot, workflowId) == WorkflowProjectHost.Free;

    /// <summary>
    /// The folder one workflow's standalone project takes beneath a container root, widened to carry
    /// the identifier when the name it prefers is already occupied.
    /// </summary>
    /// <param name="root">The container directory the project is created beneath.</param>
    /// <param name="name">The workflow name the folder is derived from.</param>
    /// <param name="workflowId">The workflow the project holds.</param>
    /// <returns>The root-relative folder name the project takes.</returns>
    public static string ResolveProjectFolder(string root, string? name, Guid workflowId)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new ArgumentException("A root is required.", nameof(root));
        }

        var preferred = ProjectFolderNameFor(name);

        if (preferred.Length == 0)
        {
            throw StandaloneWorkflowException.UnusableProjectName(name ?? workflowId.ToString());
        }

        if (Offer(root, preferred, workflowId) is WorkflowProjectHost.Free)
        {
            return preferred;
        }

        var disambiguated = FolderNameFor(name, workflowId);

        return Offer(root, disambiguated, workflowId) is WorkflowProjectHost.Free
            ? disambiguated
            : throw StandaloneWorkflowException.ProjectFolderOccupied(disambiguated);
    }

    private static WorkflowProjectHost Offer(string root, string folderName, Guid workflowId)
    {
        var host = InspectProject(Path.Combine(root, folderName), workflowId);

        return host is WorkflowProjectHost.UnpushedChanges
            ? throw StandaloneWorkflowException.ProjectHoldsUnpushedChanges(folderName)
            : host;
    }

    /// <summary>Writes the standalone workflow project declaration unless it is already current.</summary>
    /// <param name="fileAccessor">The workspace to declare.</param>
    /// <returns><see langword="true"/> when the declaration changed.</returns>
    public static bool EnsureProjectFile(IFileAccessor fileAccessor) =>
        ConnectionReferenceStore.WriteIfChanged(fileAccessor, ProjectFilePath, BuildProjectFile(), Encoding.UTF8);

    /// <summary>The body a standalone workflow project declaration carries.</summary>
    public static string BuildProjectFile() =>
        "# Standalone workflow project marker (Sync overlay; generic YAML, never MCS-parsed).\n" +
        "# Declares this workspace to hold workflows and no agent; adding an agent project supersedes it.\n" +
        $"workflowProjectVersion: {CurrentProjectVersion}\n";

    /// <summary>Whether a directory holds an agent project of its own.</summary>
    /// <param name="directory">The directory to test.</param>
    public static bool HasAgentProject(string? directory) =>
        !string.IsNullOrWhiteSpace(directory) && HasAgentProject(OpenWorkspace(directory!));

    /// <summary>Whether a workspace holds an agent project of its own.</summary>
    /// <param name="fileAccessor">The workspace to test.</param>
    public static bool HasAgentProject(IFileAccessor fileAccessor) =>
        fileAccessor is not null && AgentClassifier.AgentProjectFileNames.Any(fileName => fileAccessor.Exists(new AgentFilePath(fileName)));

    /// <summary>Whether a directory holds workflows and no agent project of its own.</summary>
    /// <param name="directory">The directory to test.</param>
    public static bool IsStandaloneWorkspace(string? directory) =>
        !string.IsNullOrWhiteSpace(directory) && IsStandaloneWorkspace(OpenWorkspace(directory!));

    /// <summary>Whether a workspace holds workflows and no agent project of its own.</summary>
    /// <param name="fileAccessor">The workspace to test.</param>
    public static bool IsStandaloneWorkspace(IFileAccessor fileAccessor) =>
        fileAccessor is not null && !HasAgentProject(fileAccessor) && (HasProjectFile(fileAccessor) || FindAll(fileAccessor).Count > 0);

    /// <summary>Whether a folder beneath the workspace names a workflow but is missing one of its files.</summary>
    /// <param name="workspaceRoot">The workspace root to search.</param>
    /// <returns><see langword="true"/> when a workflow folder is half written.</returns>
    public static bool HasIncompleteWorkflow(string workspaceRoot) =>
        !string.IsNullOrWhiteSpace(workspaceRoot) && HasIncompleteWorkflow(OpenWorkspace(workspaceRoot));

    /// <summary>Whether a folder beneath the workspace names a workflow but is missing one of its files.</summary>
    /// <param name="fileAccessor">The workspace to search.</param>
    /// <returns><see langword="true"/> when a workflow folder is half written.</returns>
    public static bool HasIncompleteWorkflow(IFileAccessor fileAccessor)
    {
        if (fileAccessor is null)
        {
            return false;
        }

        var complete = new HashSet<string>(FindAll(fileAccessor).Select(file => file.FolderName), StringComparer.Ordinal);

        return fileAccessor.ListFiles(WorkflowsFolderName, "*")
            .Select(path => FolderNameOf(path.ToString()))
            .Any(folderName => folderName is not null && !complete.Contains(folderName) && WorkflowIdOf(folderName) is not null);
    }

    private static string RelativePath(string folderName, string fileName) => $"{WorkflowsFolderName}/{folderName}/{fileName}";

    private static string? FolderNameOf(string relativePath)
    {
        var prefix = WorkflowsFolderName + "/";

        if (!relativePath.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var end = relativePath.IndexOf('/', prefix.Length);

        return end < 0 ? null : relativePath.Substring(prefix.Length, end - prefix.Length);
    }
}
