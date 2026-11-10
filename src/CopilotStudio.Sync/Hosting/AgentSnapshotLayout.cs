// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.CopilotStudio.McsCore;

namespace Microsoft.CopilotStudio.Sync.Hosting;

/// <summary>The folders a declared component collection occupies, and the declarations that name no usable folder.</summary>
/// <param name="Folders">The declared collection folders, each once, in declaration order.</param>
/// <param name="Problems">The declarations that name no usable folder, or repeat another folder ignoring case.</param>
internal sealed record AgentCollectionDeclarations(IReadOnlyList<string> Folders, IReadOnlyList<AgentSyncDiagnostic> Problems);

/// <summary>The layout rules an agent snapshot's folders and paths follow, each problem reported with a stable code and the offending folder or path.</summary>
public static class AgentSnapshotLayout
{
    /// <summary>The most characters a top-level agent or component collection folder name can hold.</summary>
    public const int MaximumFolderLength = 100;

    private const string ParentFolderPrefix = "../";

    private static readonly char[] FolderSeparators = { WorkspaceSnapshotFiles.Separator, '\\' };

    private static readonly char[] UnsafeCharacters = { ':', '*', '?', '"', '<', '>', '|' };

    /// <summary>The problem with a name used as a top-level folder, or <see langword="null"/> when it can name one.</summary>
    /// <param name="folder">The folder name.</param>
    public static AgentSyncDiagnostic? FolderProblem(string? folder) => FolderReason(folder) is { } problem ? new AgentSyncDiagnostic(problem.Code, $"The folder '{folder}' {problem.Reason}.", folder) : null;

    internal static AgentSyncDiagnostic? PathProblem(string? path, IEnumerable<string>? folders = null)
    {
        if (path is null || string.IsNullOrEmpty(path))
        {
            return new AgentSyncDiagnostic(AgentSyncDiagnosticCodes.RelativeSegmentInPath, "The snapshot holds a file with an empty path.", path);
        }

        if (path.IndexOf('\\') >= 0)
        {
            return new AgentSyncDiagnostic(AgentSyncDiagnosticCodes.BackslashInPath, $"'{path}' holds a backslash; snapshot paths are '/'-separated.", path);
        }

        if (PathHelper.IsAbsolutePath(path))
        {
            return new AgentSyncDiagnostic(AgentSyncDiagnosticCodes.AbsolutePath, $"'{path}' is absolute; snapshot paths are repository-relative.", path);
        }

        if (SyncStatePaths.IsSyncStatePath(path))
        {
            return new AgentSyncDiagnostic(AgentSyncDiagnosticCodes.SyncStateInPath, $"'{path}' holds a '.mcs' segment, which only a session's sync state may use.", path);
        }

        var segments = path.Split(WorkspaceSnapshotFiles.Separator);

        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".."))
        {
            return new AgentSyncDiagnostic(AgentSyncDiagnosticCodes.RelativeSegmentInPath, $"'{path}' holds an empty, '.' or '..' segment.", path);
        }

        if (segments.FirstOrDefault(IsReservedDeviceName) is { } deviceName)
        {
            return new AgentSyncDiagnostic(AgentSyncDiagnosticCodes.ReservedDeviceName, $"'{path}' holds the segment '{deviceName}', which Windows reserves for a device.", path);
        }

        if (segments.FirstOrDefault(segment => IsUnsafe(segment) || HasTrailingDot(segment)) is { } unsafeSegment)
        {
            return new AgentSyncDiagnostic(AgentSyncDiagnosticCodes.UnsafePath, $"'{path}' holds the segment '{unsafeSegment}', which a Windows path cannot hold as written.", path);
        }

        return segments.Length < 2 || (folders is not null && !folders.Contains(segments[0], StringComparer.Ordinal)) ? OutsideDeclaredFolders(path) : null;
    }

    internal static IReadOnlyList<AgentSyncDiagnostic> FolderSetProblems(string? agentFolder, IEnumerable<string?> collectionFolders)
    {
        var folders = new[] { agentFolder }.Concat(collectionFolders ?? throw new ArgumentNullException(nameof(collectionFolders))).ToArray();

        return folders.Select(FolderProblem).OfType<AgentSyncDiagnostic>().Concat(folders.Where(folder => FolderReason(folder) is null).OfType<string>().GroupBy(folder => folder, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1).Select(group => new AgentSyncDiagnostic(AgentSyncDiagnosticCodes.DuplicateCollection, $"The folder '{group.Key}' is named more than once, ignoring case.", group.Key))).ToArray();
    }

    /// <summary>The component collection folders an agent's references file declares as sibling top-level folders, with a problem for each declaration that names no usable folder or repeats another folder ignoring case.</summary>
    /// <param name="agentFolder">The agent's folder.</param>
    /// <param name="agentWorkspace">The agent's workspace, whose references file is read.</param>
    internal static AgentCollectionDeclarations DeclaredCollectionFolders(string agentFolder, IFileAccessor agentWorkspace)
    {
        var declared = new List<string>();
        var problems = new List<AgentSyncDiagnostic>();

        if (WorkspaceSynchronizer.ReadReferencesOrNull(agentWorkspace ?? throw new ArgumentNullException(nameof(agentWorkspace))) is not { } references)
        {
            return new AgentCollectionDeclarations(declared, problems);
        }

        foreach (var item in references.ComponentCollections)
        {
            var directory = item.Directory?.Trim().Replace('\\', WorkspaceSnapshotFiles.Separator) ?? string.Empty;
            var folder = directory.StartsWith(ParentFolderPrefix, StringComparison.Ordinal) ? directory.Substring(ParentFolderPrefix.Length).TrimEnd(WorkspaceSnapshotFiles.Separator) : null;

            if (folder is null || FolderReason(folder) is not null || folder == agentFolder)
            {
                problems.Add(new AgentSyncDiagnostic(AgentSyncDiagnosticCodes.CollectionDeclarationInvalid, $"The component collection declared as '{(string.IsNullOrEmpty(directory) ? item.SchemaName.Value : directory)}' is not declared as directory: {ParentFolderPrefix}<CollectionFolder>/.", ReferencesPathOf(agentFolder)));
            }
            else if (!declared.Contains(folder, StringComparer.Ordinal))
            {
                declared.Add(folder);
            }
        }

        problems.AddRange(FolderSetProblems(agentFolder, declared).Where(problem => problem.Code == AgentSyncDiagnosticCodes.DuplicateCollection).Select(problem => problem with { Path = ReferencesPathOf(agentFolder) }));

        return new AgentCollectionDeclarations(declared.Where(folder => !string.Equals(folder, agentFolder, StringComparison.OrdinalIgnoreCase)).GroupBy(folder => folder, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToArray(), problems);
    }

    internal static IReadOnlyList<AgentSyncDiagnostic> CollectionProblems(string collectionFolder, IReadOnlyCollection<string>? collectionFiles)
    {
        if (collectionFiles is null)
        {
            return new[] { new AgentSyncDiagnostic(AgentSyncDiagnosticCodes.CollectionFolderMissing, $"The declared component collection folder '{collectionFolder}' is not in the snapshot.", collectionFolder + WorkspaceSnapshotFiles.Separator) };
        }

        var problems = new List<AgentSyncDiagnostic>();

        if (!collectionFiles.Contains(WorkspaceSynchronizer.ComponentCollectionPath.ToString(), StringComparer.OrdinalIgnoreCase))
        {
            problems.Add(new AgentSyncDiagnostic(AgentSyncDiagnosticCodes.CollectionProjectMissing, $"The declared component collection folder '{collectionFolder}' holds no {WorkspaceSynchronizer.ComponentCollectionPath}.", collectionFolder + WorkspaceSnapshotFiles.Separator + WorkspaceSynchronizer.ComponentCollectionPath));
        }

        if (collectionFiles.Contains(WorkspaceSynchronizer.TopAgentPath.ToString(), StringComparer.OrdinalIgnoreCase))
        {
            problems.Add(new AgentSyncDiagnostic(AgentSyncDiagnosticCodes.CollectionHoldsAgent, $"The declared component collection folder '{collectionFolder}' holds an {WorkspaceSynchronizer.TopAgentPath}.", collectionFolder + WorkspaceSnapshotFiles.Separator + WorkspaceSynchronizer.TopAgentPath));
        }

        return problems;
    }

    internal static (string Code, string Reason)? FolderReason(string? folder) => folder is null || string.IsNullOrWhiteSpace(folder) ? (AgentSyncDiagnosticCodes.FolderInvalid, "is empty") : folder.IndexOfAny(FolderSeparators) >= 0 ? (AgentSyncDiagnosticCodes.FolderInvalid, "is not a single top-level folder") : folder is "." or ".." || SyncStatePaths.IsSyncStatePath(folder) ? (AgentSyncDiagnosticCodes.FolderInvalid, "is a reserved name") : IsReservedDeviceName(folder) ? (AgentSyncDiagnosticCodes.ReservedDeviceName, "is a name Windows reserves for a device") : IsUnsafe(folder) ? (AgentSyncDiagnosticCodes.UnsafePath, "holds surrounding whitespace, a control character or a character a Windows path cannot hold") : folder.StartsWith(".", StringComparison.Ordinal) ? (AgentSyncDiagnosticCodes.FolderInvalid, "starts with a '.', which marks a hidden folder") : HasTrailingDot(folder) ? (AgentSyncDiagnosticCodes.UnsafePath, "ends with a '.', which a Windows path cannot hold as written") : folder.Length > MaximumFolderLength ? (AgentSyncDiagnosticCodes.FolderInvalid, $"holds {folder.Length} characters, but a folder name can hold at most {MaximumFolderLength}") : null;

    internal static AgentSyncDiagnostic OutsideDeclaredFolders(string path) => new(AgentSyncDiagnosticCodes.OutsideDeclaredFolders, $"'{path}' lies outside the snapshot's folder and the component collection folders it declares.", path);

    internal static string ReferencesPathOf(string agentFolder) => agentFolder + WorkspaceSnapshotFiles.Separator + WorkspaceSynchronizer.ReferencesCollectionPath;

    internal static string SettingsPathOf(string agentFolder) => agentFolder + WorkspaceSnapshotFiles.Separator + WorkspaceSynchronizer.SettingsPath;

    private static bool IsReservedDeviceName(string segment) => SubAgentFolderNaming.IsWindowsReservedName(segment.Split('.')[0].TrimEnd(' '));

    private static bool IsUnsafe(string segment) => segment != segment.Trim() || segment.IndexOfAny(UnsafeCharacters) >= 0 || segment.Any(char.IsControl);

    private static bool HasTrailingDot(string segment) => segment.EndsWith(".", StringComparison.Ordinal);
}