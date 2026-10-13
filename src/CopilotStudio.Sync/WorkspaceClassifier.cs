// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.CopilotStudio.McsCore;

namespace Microsoft.CopilotStudio.Sync;

/// <summary>Classifies what a local workspace holds, including workspaces that carry workflows and no agent.</summary>
public static class WorkspaceClassifier
{
    private const string StandaloneEvidence = "standalone-workflow";

    /// <summary>The kind of workspace a directory holds.</summary>
    /// <param name="fileAccessor">The workspace to inspect.</param>
    /// <returns>The kind the workspace declares, or <see cref="WorkspaceKind.Unknown"/> when it declares neither.</returns>
    public static WorkspaceKind DetectKind(IFileAccessor fileAccessor)
    {
        if (fileAccessor is null)
        {
            return WorkspaceKind.Unknown;
        }

        if (WorkflowWorkspace.HasAgentProject(fileAccessor))
        {
            return WorkspaceKind.Agent;
        }

        return WorkflowWorkspace.HasProjectFile(fileAccessor) ? WorkspaceKind.StandaloneWorkflow : WorkspaceKind.Unknown;
    }

    /// <summary>The kind of workspace a directory holds.</summary>
    /// <param name="workspaceRoot">The workspace root to inspect.</param>
    /// <returns>The kind the workspace holds, or <see cref="WorkspaceKind.Unknown"/> when it holds neither.</returns>
    public static WorkspaceKind DetectKind(string? workspaceRoot) =>
        string.IsNullOrWhiteSpace(workspaceRoot) ? WorkspaceKind.Unknown : DetectKind(WorkflowWorkspace.OpenWorkspace(workspaceRoot!));

    /// <summary>Classifies a workspace, resolving standalone workflow workspaces that carry no agent entity.</summary>
    /// <param name="cloudEntity">The cloud agent the workspace is attached to, when it has one.</param>
    /// <param name="workspaceRoot">The workspace root to inspect.</param>
    /// <returns>The classification, carrying the workspace kind.</returns>
    public static AgentClassification Classify(BotEntity? cloudEntity, string? workspaceRoot)
    {
        var kind = DetectKind(workspaceRoot);

        return kind == WorkspaceKind.StandaloneWorkflow
            ? Standalone()
            : AgentClassifier.Classify(cloudEntity, workspaceRoot) with { Kind = kind };
    }

    /// <summary>Classifies a workspace, resolving standalone workflow workspaces that carry no agent entity.</summary>
    /// <param name="definition">The definition the workspace is attached to, when it has one.</param>
    /// <param name="workspaceRoot">The workspace root to inspect.</param>
    /// <returns>The classification, carrying the workspace kind.</returns>
    public static AgentClassification Classify(DefinitionBase? definition, string? workspaceRoot)
    {
        var kind = DetectKind(workspaceRoot);

        return kind == WorkspaceKind.StandaloneWorkflow
            ? Standalone()
            : AgentClassifier.Classify(definition, workspaceRoot) with { Kind = kind };
    }

    private static AgentClassification Standalone() =>
        AgentClassification.Recognized(AuthoringShape.CliCopilot, WorkspaceLayout.CliLayered, StandaloneEvidence) with { Kind = WorkspaceKind.StandaloneWorkflow };
}
