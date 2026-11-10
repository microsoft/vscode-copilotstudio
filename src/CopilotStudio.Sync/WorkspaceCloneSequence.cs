// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Net;
using Microsoft.Agents.Platform.Content;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.CopilotStudio.Sync;

/// <summary>Clones an agent and the component collections it links into folders the caller chose, each with sync info scoped to it, then points the references between them at those folders.</summary>
public static class WorkspaceCloneSequence
{
    /// <summary>Clones each asset into its folder, then rewrites the references between the cloned workspaces.</summary>
    /// <param name="synchronizer">Clones each workspace and rewrites its references.</param>
    /// <param name="assets">The operation context of each asset with the folder it is cloned into, the agent first.</param>
    /// <param name="syncInfo">The agent's sync info.</param>
    /// <param name="dataverseClient">The client, already targeting the agent's environment.</param>
    /// <param name="cancellationToken">Cancels the clone between assets.</param>
    public static async Task CloneAsync(IWorkspaceSynchronizer synchronizer, IReadOnlyList<KeyValuePair<AuthoringOperationContextBase, DirectoryPath>> assets, AgentSyncInfo syncInfo, ISyncDataverseClient dataverseClient, CancellationToken cancellationToken)
    {
        if (synchronizer is null)
        {
            throw new ArgumentNullException(nameof(synchronizer));
        }

        if (assets is null)
        {
            throw new ArgumentNullException(nameof(assets));
        }

        var referenceTracker = new ReferenceTracker();

        foreach (var asset in assets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var assetSyncInfo = ScopeTo(syncInfo, asset.Key);
            await synchronizer.SaveSyncInfoAsync(asset.Value, assetSyncInfo).ConfigureAwait(false);
            await synchronizer.CloneChangesAsync(asset.Value, referenceTracker, asset.Key, dataverseClient, assetSyncInfo, cancellationToken).ConfigureAwait(false);
        }

        foreach (var asset in assets)
        {
            await synchronizer.ApplyTouchupsAsync(asset.Value, referenceTracker, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The sync info of an agent in its environment, reading the environment's solution versions.</summary>
    /// <param name="dataverseClient">The client, already targeting the agent's environment.</param>
    /// <param name="agentId">The agent.</param>
    /// <param name="dataverseEndpoint">The Dataverse organization of the agent's environment.</param>
    /// <param name="environmentId">The agent's environment.</param>
    /// <param name="tenantId">The tenant the environment belongs to, or <see cref="Guid.Empty"/> when it is not known.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static async Task<AgentSyncInfo> ForAgentAsync(ISyncDataverseClient dataverseClient, Guid agentId, Uri dataverseEndpoint, string environmentId, Guid tenantId, CancellationToken cancellationToken)
    {
        if (dataverseClient is null)
        {
            throw new ArgumentNullException(nameof(dataverseClient));
        }

        return new AgentSyncInfo { AgentId = agentId, DataverseEndpoint = dataverseEndpoint, EnvironmentId = environmentId, SolutionVersions = await dataverseClient.GetSolutionVersionsAsync(cancellationToken).ConfigureAwait(false), AccountInfo = new AccountInfo { TenantId = tenantId } };
    }

    internal static async Task<Guid> FindAgentAsync(ISyncDataverseClient dataverseClient, Guid? agentId, string schemaName, Func<DataverseRequestException?, Exception> notFound, CancellationToken cancellationToken)
    {
        if (agentId is { } id)
        {
            return id;
        }

        var found = await dataverseClient.GetAgentIdBySchemaNameAsync(schemaName, cancellationToken).ConfigureAwait(false);

        return found != Guid.Empty ? found : throw notFound(null);
    }

    internal static async Task<AgentInfo> ReadAgentAsync(ISyncDataverseClient dataverseClient, Guid agentId, Func<DataverseRequestException?, Exception> notFound, CancellationToken cancellationToken)
    {
        try
        {
            return await dataverseClient.GetAgentInfoAsync(agentId, cancellationToken).ConfigureAwait(false);
        }
        catch (DataverseRequestException failure) when (failure.StatusCode == HttpStatusCode.NotFound)
        {
            throw notFound(failure);
        }
    }

    internal static AgentSyncInfo ScopeTo(AgentSyncInfo syncInfo, AuthoringOperationContextBase operationContext) => operationContext is BotComponentCollectionAuthoringOperationContext collection ? ForComponentCollection(syncInfo, collection.BotComponentCollectionReference.CdsId) : syncInfo;

    internal static AgentSyncInfo ForComponentCollection(AgentSyncInfo syncInfo, Guid componentCollectionId)
    {
        if (syncInfo is null)
        {
            throw new ArgumentNullException(nameof(syncInfo));
        }

        return new AgentSyncInfo
        {
            ComponentCollectionId = componentCollectionId,
            DataverseEndpoint = syncInfo.DataverseEndpoint,
            EnvironmentId = syncInfo.EnvironmentId,
            EnvironmentDisplayName = syncInfo.EnvironmentDisplayName,
            SolutionVersions = syncInfo.SolutionVersions,
            AccountInfo = syncInfo.AccountInfo,
            AgentManagementEndpoint = syncInfo.AgentManagementEndpoint,
        };
    }
}
