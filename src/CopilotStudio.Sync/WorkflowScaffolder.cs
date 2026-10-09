// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.CopilotStudio.Sync;

/// <summary>Writes a new standalone workflow from the definition template embedded in this assembly.</summary>
public static class WorkflowScaffolder
{
    private const string DefinitionResource = "Templates/mcs-workflow/workflow.json";

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

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A workflow name is required.", nameof(name));
        }

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

        return new[] { $"{relativeFolder}/{WorkflowWorkspace.DefinitionFileName}", $"{relativeFolder}/{WorkflowWorkspace.MetadataFileName}" };
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

        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);

        return reader.ReadToEnd()
            .Replace("{{name}}", JsonEscape(name))
            .Replace("{{startNodeId}}", $"start-{Guid.NewGuid()}")
            .Replace("{{responseNodeId}}", $"builtinFunction-{Guid.NewGuid()}");
    }

    private static string JsonEscape(string value) => JsonSerializer.Serialize(value).Trim('"');
}
