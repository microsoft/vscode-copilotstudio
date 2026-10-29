// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.CopilotStudio.Sync;

/// <summary>A workflow scaffolded into its own standalone project beneath a container root.</summary>
/// <param name="ProjectFolder">The root-relative folder the project took.</param>
/// <param name="ProjectRoot">The absolute project directory.</param>
/// <param name="FolderName">The root-relative folder holding the workflow's files.</param>
/// <param name="WrittenFiles">The root-relative paths written, definition first.</param>
public sealed record WorkflowScaffoldedProject(string ProjectFolder, string ProjectRoot, string FolderName, IReadOnlyList<string> WrittenFiles);

/// <summary>Writes a new standalone workflow from the definition template embedded in this assembly.</summary>
public static class WorkflowScaffolder
{
    private const string DefinitionResource = "Templates/mcs-workflow/workflow.json";

    /// <summary>Writes a new workflow into its own standalone project beneath a container root.</summary>
    /// <param name="root">The container directory the project is created beneath.</param>
    /// <param name="name">The workflow's display name.</param>
    /// <param name="workflowId">The identifier the workflow takes.</param>
    /// <returns>The project that was written and the root-relative paths it holds.</returns>
    public static WorkflowScaffoldedProject ScaffoldProject(string root, string name, Guid workflowId)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new ArgumentException("A root is required.", nameof(root));
        }

        RequireValidName(name);

        var projectFolder = WorkflowWorkspace.ResolveProjectFolder(root, name, workflowId);
        var projectRoot = Path.Combine(root, projectFolder);

        Directory.CreateDirectory(projectRoot);

        return new WorkflowScaffoldedProject(projectFolder, projectRoot, $"{projectFolder}/{WorkflowWorkspace.RelativeFolderFor(name, workflowId)}", Scaffold(projectRoot, name, workflowId).Select(path => $"{projectFolder}/{path}").ToArray());
    }

    /// <summary>Writes the definition and metadata pair for a new workflow.</summary>
    /// <param name="workspaceRoot">The workflow workspace root.</param>
    /// <param name="name">The workflow's display name.</param>
    /// <param name="workflowId">The identifier the workflow takes.</param>
    /// <returns>The workspace-relative paths written, definition first.</returns>
    public static IReadOnlyList<string> Scaffold(string workspaceRoot, string name, Guid workflowId)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            throw new ArgumentException("A workspace root is required.", nameof(workspaceRoot));
        }

        RequireValidName(name);

        var relativeFolder = WorkflowWorkspace.RelativeFolderFor(name, workflowId);
        var folderPath = Path.Combine(WorkflowWorkspace.WorkflowsDirectory(workspaceRoot), WorkflowWorkspace.FolderNameFor(name, workflowId));
        var definitionPath = Path.Combine(folderPath, WorkflowWorkspace.DefinitionFileName);
        var metadataPath = Path.Combine(folderPath, WorkflowWorkspace.MetadataFileName);

        if (File.Exists(definitionPath) || File.Exists(metadataPath))
        {
            throw new InvalidOperationException($"A workflow already occupies '{relativeFolder}'.");
        }

        Directory.CreateDirectory(folderPath);
        WorkflowDefinitionFile.WriteIfChanged(definitionPath, Definition(name));
        WorkflowDefinitionFile.WriteIfChanged(metadataPath, WorkflowMetadataFile.Write(NewMetadata(name, workflowId, relativeFolder)));

        var written = new List<string> { $"{relativeFolder}/{WorkflowWorkspace.DefinitionFileName}", $"{relativeFolder}/{WorkflowWorkspace.MetadataFileName}" };

        if (!WorkflowWorkspace.HasAgentProject(workspaceRoot))
        {
            var workspace = WorkflowWorkspace.OpenWorkspace(workspaceRoot);

            if (WorkflowWorkspace.EnsureLayoutMarker(workspace))
            {
                written.Add(AgentClassifier.WorkspaceLayoutMarkerFileName);
            }

            if (WorkflowWorkspace.EnsureProjectFile(workspace))
            {
                written.Add(WorkflowWorkspace.ProjectFileName);
            }
        }

        return written;
    }

    /// <summary>The metadata a newly scaffolded workflow starts from.</summary>
    /// <param name="name">The workflow's display name.</param>
    /// <param name="workflowId">The identifier the workflow takes.</param>
    /// <param name="relativeFolder">The workspace-relative folder holding the workflow.</param>
    public static SyncDataverseClient.WorkflowMetadata NewMetadata(string name, Guid workflowId, string relativeFolder) => new()
    {
        JsonFileName = $"{relativeFolder}/{WorkflowWorkspace.DefinitionFileName}",
        WorkflowId = workflowId,
        Name = name,
        Type = 1,
        Subprocess = false,
        Category = 5,
        Mode = 0,
        Scope = 4,
        OnDemand = false,
        TriggerOnCreate = false,
        TriggerOnDelete = false,
        AsyncAutodelete = false,
        SyncWorkflowLogOnFailure = false,
        StateCode = 1,
        StatusCode = 2,
        RunAs = 1,
        IsTransacted = true,
        IntroducedVersion = "1.0",
        IsCustomizable = new SyncDataverseClient.ManagedProperty { Value = true, CanBeChanged = true, ManagedPropertyLogicalName = "iscustomizableanddeletable" },
        BusinessProcessType = 0,
        IsCustomProcessingStepAllowedForOtherPublishers = new SyncDataverseClient.ManagedProperty { Value = true, CanBeChanged = true, ManagedPropertyLogicalName = "canbedeleted" },
        ModernFlowType = 1,
        PrimaryEntity = "none",
    };

    private static string Definition(string name)
    {
        var assembly = typeof(WorkflowScaffolder).Assembly;
        var resource = assembly.GetManifestResourceNames().FirstOrDefault(candidate => candidate.Replace('\\', '/') == DefinitionResource)
            ?? throw new InvalidOperationException($"The workflow template '{DefinitionResource}' is missing from {assembly.GetName().Name}.");

        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"The workflow template '{resource}' could not be opened.");
        using var reader = new StreamReader(stream, Encoding.UTF8);

        return reader.ReadToEnd()
            .Replace("{{startNodeId}}", $"start-{Guid.NewGuid()}")
            .Replace("{{responseNodeId}}", $"builtinFunction-{Guid.NewGuid()}")
            .Replace("{{name}}", JsonEscape(name));
    }

    private static string JsonEscape(string value) => JsonEncodedText.Encode(value).ToString();

    private static void RequireValidName(string name)
    {
        if (WorkflowNaming.Validate(name) is { } reason)
        {
            throw StandaloneWorkflowException.InvalidName(name, reason);
        }
    }
}
