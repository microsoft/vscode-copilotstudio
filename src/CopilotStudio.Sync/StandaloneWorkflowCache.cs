// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Collections.Immutable;
using Microsoft.Agents.ObjectModel;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.McsCore.Yaml;
using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.CopilotStudio.Sync;

/// <summary>
/// Holds the service's own copy of each workflow a standalone workspace tracks, so local edits can
/// be told apart from remote ones. Shares the agent cache file, so a workspace that later gains an
/// agent keeps every copy it already had.
/// </summary>
public static class StandaloneWorkflowCache
{
    /// <summary>The flows the service last returned, empty when the workspace keeps no cache.</summary>
    /// <param name="fileAccessor">The workspace holding the cache.</param>
    /// <returns>The cached flows, in the order the cache holds them.</returns>
    public static ImmutableArray<CloudFlowDefinition> Flows(IFileAccessor fileAccessor)
    {
        if (fileAccessor is null)
        {
            throw new ArgumentNullException(nameof(fileAccessor));
        }

        return WorkspaceSynchronizer.ReadCloudCacheSnapshot(fileAccessor, allowMissing: true)?.Flows ?? ImmutableArray<CloudFlowDefinition>.Empty;
    }

    /// <summary>The service's copy of a workflow at the last sync, or null when none was kept.</summary>
    /// <param name="fileAccessor">The workspace holding the cache.</param>
    /// <param name="workflowId">The workflow to read.</param>
    /// <returns>The cached copy, carrying the definition the service returned.</returns>
    public static SyncDataverseClient.WorkflowMetadata? Read(IFileAccessor fileAccessor, Guid workflowId)
    {
        if (Find(Flows(fileAccessor), workflowId) is not { } flow)
        {
            return null;
        }

        SyncDataverseClient.WorkflowMetadata? cached;

        try
        {
            cached = WorkflowMetadataFile.ReadStrict(WorkspaceSynchronizer.GetWorkflowMetadata(flow));
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            return null;
        }

        if (cached is not null)
        {
            cached.ClientData = WorkspaceSynchronizer.GetRawClientData(flow);
        }

        return cached;
    }

    /// <summary>
    /// Writes the service's copy of a workflow. Only pass something the service returned: a local
    /// definition stored here would make the next comparison report no change where there was one.
    /// </summary>
    /// <param name="fileAccessor">The workspace holding the cache.</param>
    /// <param name="serverMetadata">The workflow exactly as the service returned it.</param>
    /// <returns><see langword="true"/> when the cache changed.</returns>
    public static bool Write(IFileAccessor fileAccessor, SyncDataverseClient.WorkflowMetadata serverMetadata)
    {
        if (fileAccessor is null)
        {
            throw new ArgumentNullException(nameof(fileAccessor));
        }

        if (serverMetadata is null)
        {
            throw new ArgumentNullException(nameof(serverMetadata));
        }

        var (flow, _) = WorkspaceSynchronizer.GetFlowDefinition(serverMetadata);

        return Store(fileAccessor, serverMetadata.WorkflowId, flow);
    }

    /// <summary>Drops the service's copy of a workflow.</summary>
    /// <param name="fileAccessor">The workspace holding the cache.</param>
    /// <param name="workflowId">The workflow to forget.</param>
    /// <returns><see langword="true"/> when something was removed.</returns>
    public static bool Remove(IFileAccessor fileAccessor, Guid workflowId)
    {
        if (fileAccessor is null)
        {
            throw new ArgumentNullException(nameof(fileAccessor));
        }

        return Store(fileAccessor, workflowId, null);
    }

    /// <summary>Whether a workflow on disk differs from the service's copy of it.</summary>
    /// <param name="fileAccessor">The workspace holding both.</param>
    /// <param name="workflow">The workflow on disk.</param>
    /// <returns><see langword="true"/> when the workspace carries an edit the service has not seen.</returns>
    public static bool HasLocalChange(IFileAccessor fileAccessor, WorkflowFile workflow)
    {
        if (workflow is null)
        {
            throw new ArgumentNullException(nameof(workflow));
        }

        return Find(Flows(fileAccessor), workflow.WorkflowId) is not { } cached || Differs(fileAccessor, workflow, cached);
    }

    /// <summary>Whether a workflow on disk differs from a copy the service returned.</summary>
    /// <param name="fileAccessor">The workspace holding the workflow.</param>
    /// <param name="workflow">The workflow on disk.</param>
    /// <param name="reference">The copy to compare against, or <see langword="null"/> when there is none.</param>
    /// <returns><see langword="true"/> when the workspace carries content the reference does not.</returns>
    public static bool Differs(IFileAccessor fileAccessor, WorkflowFile workflow, SyncDataverseClient.WorkflowMetadata? reference)
    {
        if (workflow is null)
        {
            throw new ArgumentNullException(nameof(workflow));
        }

        if (reference is null)
        {
            return true;
        }

        var (flow, _) = WorkspaceSynchronizer.GetFlowDefinition(reference);

        return Differs(fileAccessor, workflow, flow);
    }

    private static bool Differs(IFileAccessor fileAccessor, WorkflowFile workflow, CloudFlowDefinition reference)
    {
        var local = ConnectionReferenceStore.ReadTextOrNull(fileAccessor, workflow.DefinitionPath);

        if (!string.Equals(WorkflowDefinitionFile.Canonicalize(local), WorkflowDefinitionFile.Canonicalize(WorkspaceSynchronizer.GetRawClientData(reference)), StringComparison.Ordinal))
        {
            return true;
        }

        if (ReadMetadataOrNull(fileAccessor, workflow.MetadataPath) is not { } declared)
        {
            return true;
        }

        declared.ClientData = local;

        var (flow, _) = WorkspaceSynchronizer.GetFlowDefinition(declared);

        return !McsYamlComparer.DocumentsMatch(WorkspaceSynchronizer.GetWorkflowMetadata(flow), WorkspaceSynchronizer.GetWorkflowMetadata(reference));
    }

    private static bool Store(IFileAccessor fileAccessor, Guid workflowId, CloudFlowDefinition? flow)
    {
        var snapshot = WorkspaceSynchronizer.ReadCloudCacheSnapshot(fileAccessor, allowMissing: true) ?? BotDefinition.Empty;
        var existing = snapshot.Flows;
        var updated = ImmutableArray.CreateBuilder<CloudFlowDefinition>();
        var replaced = false;

        foreach (var candidate in existing)
        {
            if (candidate.WorkflowId.Value != workflowId)
            {
                updated.Add(candidate);

                continue;
            }

            replaced = true;

            if (flow is not null)
            {
                updated.Add(flow);
            }
        }

        if (!replaced && flow is not null)
        {
            updated.Add(flow);
        }

        var result = updated.ToImmutable();

        if (SameContent(existing, result))
        {
            return false;
        }

        WorkspaceSynchronizer.WriteCloudCache(fileAccessor, snapshot.WithFlows(result));

        return true;
    }

    private static bool SameContent(ImmutableArray<CloudFlowDefinition> left, ImmutableArray<CloudFlowDefinition> right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        for (var index = 0; index < left.Length; index++)
        {
            if (left[index].WorkflowId.Value != right[index].WorkflowId.Value
                || !string.Equals(WorkspaceSynchronizer.GetWorkflowMetadata(left[index]), WorkspaceSynchronizer.GetWorkflowMetadata(right[index]), StringComparison.Ordinal)
                || !string.Equals(WorkspaceSynchronizer.GetRawClientData(left[index]), WorkspaceSynchronizer.GetRawClientData(right[index]), StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static CloudFlowDefinition? Find(ImmutableArray<CloudFlowDefinition> flows, Guid workflowId)
    {
        foreach (var flow in flows)
        {
            if (flow.WorkflowId.Value == workflowId)
            {
                return flow;
            }
        }

        return null;
    }

    private static SyncDataverseClient.WorkflowMetadata? ReadMetadataOrNull(IFileAccessor fileAccessor, AgentFilePath path)
    {
        try
        {
            return ConnectionReferenceStore.ReadTextOrNull(fileAccessor, path) is { } yaml ? WorkflowMetadataFile.ReadStrict(yaml) : null;
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            return null;
        }
    }
}
