// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Hosting;
using InMemoryWorkspace = Microsoft.CopilotStudio.McsCore.InMemoryFileAccessor;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

internal static class Scaffolds
{
    public static AgentWorkspaceSnapshot Agent(string folder, string displayName, string schemaName, string authoringMode, string? instructions = null) => Read(folder, workspace => AgentScaffolder.Scaffold(workspace, authoringMode, displayName, schemaName, instructions ?? string.Empty));

    public static AgentWorkspaceSnapshot Workflow(string folder, string name, Guid workflowId) => Read(folder, workspace => WorkflowScaffolder.Scaffold(workspace, name, workflowId));

    private static AgentWorkspaceSnapshot Read(string folder, Action<IFileAccessor> scaffold)
    {
        var workspace = new InMemoryWorkspace();
        scaffold(workspace);

        return WorkspaceSnapshotFiles.Read(folder, new[] { new KeyValuePair<string, IFileAccessor>(folder, workspace) });
    }
}