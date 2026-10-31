// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Collections.Immutable;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.CopilotStudio.Sync;

/// <summary>Picks out the local changes a push saved to the environment.</summary>
public static class PushedChanges
{
    /// <summary>Removes every change owned by a custom connector, workflow or AI prompt the push did not save.</summary>
    /// <param name="changes">The local changes the push sent.</param>
    /// <param name="connectors">The outcome of the custom connector push.</param>
    /// <param name="workflowResponses">The outcome of each workflow the push sent.</param>
    /// <param name="promptResponses">The outcome of each AI prompt the push sent.</param>
    /// <returns>The changes the environment accepted.</returns>
    public static ImmutableArray<Change> WithoutFailedAssets(this ImmutableArray<Change> changes, CustomConnectorPushResult connectors, ImmutableArray<WorkflowResponse> workflowResponses, ImmutableArray<SyncDataverseClient.AIPromptResponse> promptResponses)
    {
        var failedAssetSchemaNames = connectors.Failed.Select(change => SyncedAssetSchemaName.GetAssetSchemaName(change.SchemaName)).Concat(workflowResponses.Where(response => !string.IsNullOrEmpty(response.ErrorMessage)).Select(response => WorkflowLayout.SchemaNamePrefix + response.WorkflowId)).Concat(promptResponses.Where(response => !string.IsNullOrEmpty(response.ErrorMessage)).Select(response => AiPromptLayout.SchemaNamePrefix + response.AIModelId)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return changes.RemoveAll(change => failedAssetSchemaNames.Contains(SyncedAssetSchemaName.GetAssetSchemaName(change.SchemaName)));
    }
}
