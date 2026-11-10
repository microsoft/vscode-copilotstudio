// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.CopilotStudio.McsCore;

namespace Microsoft.CopilotStudio.Sync.Hosting;

internal static class AgentSnapshotValidator
{
    private static readonly string[] AgentProbeFiles = { WorkspaceSynchronizer.SettingsPath.ToString(), AgentClassifier.WorkspaceLayoutMarkerFileName, WorkspaceSynchronizer.ReferencesCollectionPath.ToString() };

    private static readonly string WorkflowProbePrefix = WorkflowWorkspace.WorkflowsFolderName + WorkspaceSnapshotFiles.Separator;

    public static void RequireFolder(string? folder, string parameterName)
    {
        if (AgentSnapshotLayout.FolderProblem(folder) is { } problem)
        {
            throw new ArgumentException(problem.Message, parameterName);
        }
    }

    public static string? AuthoringModeOf(AuthoringShape shape) => shape switch
    {
        AuthoringShape.CliCopilot => AgentAuthoringModes.CliCopilot,
        AuthoringShape.Classic => AgentAuthoringModes.Classic,
        _ => null,
    };

    public static AgentWorkspaceValidation ValidateAgent(AgentWorkspaceSnapshot snapshot)
    {
        var scan = Scan(snapshot, IsAgentProbe);

        if (scan.Stopped)
        {
            return new AgentWorkspaceValidation(false, scan.Diagnostics, Array.Empty<string>(), null);
        }

        var probe = scan.BuildProbeWorkspace();

        if (!scan.Folders.TryGetValue(snapshot.Folder, out var agentFiles) || !agentFiles.Contains(WorkspaceSynchronizer.SettingsPath.ToString()))
        {
            scan.Diagnostics.Add(new AgentSyncDiagnostic(AgentSyncDiagnosticCodes.AgentProjectMissing, $"The agent folder '{snapshot.Folder}' holds no {WorkspaceSynchronizer.SettingsPath}.", AgentSnapshotLayout.SettingsPathOf(snapshot.Folder)));
        }

        var authoringMode = AuthoringModeOf(AgentClassifier.DetectAuthoringShapeFromFolder(probe));
        var declarations = AgentSnapshotLayout.DeclaredCollectionFolders(snapshot.Folder, probe);
        var declared = declarations.Folders;
        scan.Diagnostics.AddRange(declarations.Problems);

        if (declared.Count > 0 && authoringMode == AgentAuthoringModes.CliCopilot)
        {
            scan.Diagnostics.Add(new AgentSyncDiagnostic(AgentSyncDiagnosticCodes.CliAgentDeclaresCollection, $"The {AgentAuthoringModes.CliCopilot} agent in '{snapshot.Folder}' declares component collections, which only classic agents can use.", AgentSnapshotLayout.ReferencesPathOf(snapshot.Folder)));
        }

        foreach (var collectionFolder in declared)
        {
            scan.Diagnostics.AddRange(AgentSnapshotLayout.CollectionProblems(collectionFolder, scan.Folders.TryGetValue(collectionFolder, out var collectionFiles) ? collectionFiles : null));
        }

        var collectionNames = ComponentCollectionFolders.ReadSchemaNames(declared.Where(scan.CollectionProjects.ContainsKey).Select(folder => new KeyValuePair<string, IFileAccessor>(folder, scan.BuildCollectionProbe(folder))));
        scan.Diagnostics.AddRange(collectionNames.Problems.Select(CollectionNameProblem));

        foreach (var folder in scan.Folders.Keys.Where(folder => folder != snapshot.Folder && !declared.Contains(folder, StringComparer.Ordinal)).OrderBy(folder => folder, StringComparer.Ordinal))
        {
            scan.Diagnostics.Add(AgentSnapshotLayout.OutsideDeclaredFolders(folder + WorkspaceSnapshotFiles.Separator));
        }

        return new AgentWorkspaceValidation(scan.Diagnostics.Count == 0, scan.Diagnostics, declared, authoringMode) { DeclaredCollections = collectionNames.Folders };
    }

    public static WorkflowWorkspaceValidation ValidateWorkflowProject(AgentWorkspaceSnapshot snapshot)
    {
        var scan = Scan(snapshot, IsWorkflowProbe);

        if (scan.Stopped)
        {
            return new WorkflowWorkspaceValidation(false, scan.Diagnostics, null);
        }

        scan.Diagnostics.AddRange(scan.Folders.Keys.Where(folder => folder != snapshot.Folder).OrderBy(folder => folder, StringComparer.Ordinal).Select(folder => AgentSnapshotLayout.OutsideDeclaredFolders(folder + WorkspaceSnapshotFiles.Separator)));
        var probe = scan.BuildProbeWorkspace();
        var workflows = WorkflowWorkspace.FindAll(probe);
        var problem = WorkflowWorkspace.HasAgentProject(probe) ? $"A standalone workflow folder holds no agent, and '{snapshot.Folder}' holds one." : workflows.Count == 1 ? null : $"A standalone workflow folder holds exactly one workflow beneath {WorkflowWorkspace.WorkflowsFolderName}/, and '{snapshot.Folder}' holds {workflows.Count}.";

        if (problem is not null)
        {
            scan.Diagnostics.Add(new AgentSyncDiagnostic(AgentSyncDiagnosticCodes.WorkflowProjectInvalid, problem, snapshot.Folder + WorkspaceSnapshotFiles.Separator));
        }

        var workflow = problem is null ? workflows[0] : null;

        return new WorkflowWorkspaceValidation(scan.Diagnostics.Count == 0, scan.Diagnostics, workflow?.WorkflowId.ToString()) { Workflow = workflow };
    }

    private static AgentSyncDiagnostic CollectionNameProblem(ComponentCollectionFolderProblem problem) => new(problem.Kind == ComponentCollectionFolderProblemKind.SchemaNameRepeated ? AgentSyncDiagnosticCodes.CollectionSchemaNameRepeated : AgentSyncDiagnosticCodes.CollectionSchemaNameMissing, ComponentCollectionFolders.Describe(problem), problem.Folder + WorkspaceSnapshotFiles.Separator + WorkspaceSynchronizer.ComponentCollectionPath) { Subject = problem.SchemaName };

    private static SnapshotScan Scan(AgentWorkspaceSnapshot snapshot, Func<string, bool> isProbe)
    {
        if (snapshot is null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        var scan = new SnapshotScan();

        if (AgentSnapshotLayout.FolderProblem(snapshot.Folder) is { } folderProblem)
        {
            scan.Stopped = true;
            scan.Diagnostics.Add(folderProblem);
        }

        if (snapshot.Files is not { Count: > 0 })
        {
            scan.Stopped = true;
            scan.Diagnostics.Add(new AgentSyncDiagnostic(AgentSyncDiagnosticCodes.EmptySnapshot, "The snapshot holds no file."));
        }

        if (scan.Stopped || snapshot.Files is not { } files)
        {
            return scan;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files.OrderBy(file => file.Key, StringComparer.Ordinal))
        {
            if (AgentSnapshotLayout.PathProblem(file.Key) is { } pathProblem)
            {
                scan.Diagnostics.Add(pathProblem);
                continue;
            }

            if (!seen.Add(file.Key))
            {
                scan.Diagnostics.Add(new AgentSyncDiagnostic(AgentSyncDiagnosticCodes.CaseOnlyDuplicate, $"'{file.Key}' differs from another path only in case.", file.Key));
                continue;
            }

            var separator = file.Key.IndexOf(WorkspaceSnapshotFiles.Separator);
            var folder = file.Key.Substring(0, separator);
            var relativePath = file.Key.Substring(separator + 1);

            if (!scan.Folders.TryGetValue(folder, out var folderFiles))
            {
                folderFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                scan.Folders[folder] = folderFiles;
            }

            folderFiles.Add(relativePath);

            if (folder != snapshot.Folder && string.Equals(relativePath, WorkspaceSynchronizer.ComponentCollectionPath.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                scan.CollectionProjects[folder] = file.Value;
            }

            if (folder == snapshot.Folder && isProbe(relativePath))
            {
                scan.Probes[relativePath] = file.Value;
            }
        }

        return scan;
    }

    private static bool IsAgentProbe(string relativePath) => AgentProbeFiles.Contains(relativePath, StringComparer.OrdinalIgnoreCase);

    private static bool IsWorkflowProbe(string relativePath) => relativePath.StartsWith(WorkflowProbePrefix, StringComparison.OrdinalIgnoreCase) || AgentClassifier.AgentProjectFileNames.Contains(relativePath, StringComparer.OrdinalIgnoreCase);

    private sealed class SnapshotScan
    {
        public List<AgentSyncDiagnostic> Diagnostics { get; } = new();

        public Dictionary<string, HashSet<string>> Folders { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, ReadOnlyMemory<byte>> Probes { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, ReadOnlyMemory<byte>> CollectionProjects { get; } = new(StringComparer.Ordinal);

        public bool Stopped { get; set; }

        public IFileAccessor BuildProbeWorkspace()
        {
            var probe = new InMemoryFileAccessor();

            foreach (var probeFile in Probes)
            {
                probe.Write(new AgentFilePath(probeFile.Key), probeFile.Value);
            }

            return probe;
        }

        public IFileAccessor BuildCollectionProbe(string folder)
        {
            var probe = new InMemoryFileAccessor();
            probe.Write(WorkspaceSynchronizer.ComponentCollectionPath, CollectionProjects[folder]);

            return probe;
        }
    }
}
