namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.CopilotStudio.Sync;
    using Microsoft.CopilotStudio.Sync.Dataverse;
    using Microsoft.PowerPlatformLS.Contracts.FileLayout;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using Microsoft.PowerPlatformLS.Impl.PullAgent.Auth;
    using System;
    using System.Collections.Immutable;
    using System.Threading;
    using System.Threading.Tasks;

    internal static class ConnectionHelper
    {
        /// <summary>
        /// Sets up Dataverse connection infrastructure and enriches ambient agent context.
        /// Connection setup (tokens, URLs) is synchronous; agent context resolution is async
        /// (may read from disk). The returned Task must be awaited — do not fire-and-forget.
        /// Agent context propagation is safe across async boundaries because LspRequestContext
        /// uses a mutable reference holder (not value-type AsyncLocal writes).
        /// </summary>
#pragma warning disable VSTHRD200 // Not suffixed with Async: synchronous preamble + tail-call to SetAgentContextAsync
        public static Task ApplyConnectionContext(
            IIslandControlPlaneService islandControlPlaneService,
            ITokenManager dataverseTokenManager,
            LspDataverseHttpClientAccessor dataverseHttpClientAccessor,
            ISyncDataverseClient dataverseClient,
            DataverseRequest request,
            IWorkspaceSynchronizer synchronizer,
            IMcsWorkspace? workspace = null,
            AgentInfo? agentInfo = null)
        {
            islandControlPlaneService.SetConnectionContext(request.EnvironmentInfo.AgentManagementUrl, request.AccountInfo.ClusterCategory);
            dataverseTokenManager.SetTokens(request.DataverseAccessToken, request.CopilotStudioAccessToken);
            dataverseHttpClientAccessor.SetDataverseUrl(new Uri(request.EnvironmentInfo.DataverseUrl));
            dataverseClient.SetDataverseUrl(request.EnvironmentInfo.DataverseUrl);

            return SetAgentContextAsync(request, synchronizer, workspace, agentInfo);
        }
#pragma warning restore VSTHRD200

        /// <summary>
        /// Sets ambient agent context from persisted sync metadata (no Dataverse connection setup).
        /// Priority: syncInfo (from disk) > workspace definition > request env.
        /// Safe to call from async methods because LspRequestContext uses a mutable reference holder
        /// (not value-type AsyncLocal writes) so mutations propagate across async boundaries.
        /// </summary>
        public static async Task SetAgentContextAsync(DataverseRequest request, IWorkspaceSynchronizer synchronizer, IMcsWorkspace? workspace = null, AgentInfo? agentInfo = null)
        {
            var agentName = agentInfo?.DisplayName ?? (workspace?.Definition as BotDefinition)?.Entity?.DisplayName;
            var agentId = agentInfo?.AgentId.ToString();
            var envName = request.EnvironmentInfo.DisplayName;
            var envId = request.EnvironmentInfo.EnvironmentId;

            if (workspace != null && synchronizer.IsSyncInfoAvailable(workspace.FolderPath))
            {
                var syncInfo = await synchronizer.GetSyncInfoAsync(workspace.FolderPath);
                agentId = syncInfo.AgentId?.ToString();
                envName = syncInfo.EnvironmentDisplayName ?? envName;
                envId = syncInfo.EnvironmentId ?? envId;
            }

            LspRequestContext.SetAgentContext(agentName, agentId, envName, envId);
        }

        public static AgentSyncInfo BuildDefaultSyncInfo(DataverseRequest request) => new()
        {
            AgentId = Guid.Empty,
            DataverseEndpoint = new Uri(request.EnvironmentInfo.DataverseUrl),
            EnvironmentId = request.EnvironmentInfo.EnvironmentId,
            EnvironmentDisplayName = request.EnvironmentInfo.DisplayName,
            AccountInfo = request.AccountInfo,
            SolutionVersions = request.SolutionVersions,
            AgentManagementEndpoint = new Uri(request.EnvironmentInfo.AgentManagementUrl)
        };

        public static PowerAppsContext BuildCatalogContext(DataverseRequest request, string? connectionsAccessToken) => new()
        {
            AccessToken = connectionsAccessToken ?? string.Empty,
            EnvironmentId = request.EnvironmentInfo.EnvironmentId,
            ClusterCategory = request.AccountInfo.ClusterCategory,
        };

        public static async Task ProvisionConnectionsAsync(IWorkspaceSynchronizer synchronizer, DirectoryPath workspaceFolder, DefinitionBase definition, ISyncDataverseClient dataverseClient, CancellationToken cancellationToken)
        {
            var connectorPushResult = await synchronizer.PushCustomConnectorsAsync(workspaceFolder, dataverseClient, cancellationToken);
            await synchronizer.ProvisionConnectionReferencesAsync(workspaceFolder, definition, dataverseClient, cancellationToken, connectorPushResult.PushedRowIds);
        }

        public static async Task<(ImmutableArray<WorkflowResponse> WorkflowResponse, CloudFlowMetadata? CloudFlowMetadata, ImmutableArray<SyncDataverseClient.AIPromptResponse> AIPromptResponse, ImmutableArray<SyncDataverseClient.AIPromptMetadata> AIPromptMetadata)> UpsertAgentScopedAssetsAsync(IWorkspaceSynchronizer synchronizer, DirectoryPath workspaceFolder, DefinitionBase definition, ISyncDataverseClient dataverseClient, Guid? agentId, WorkflowActivationMode activationMode, CancellationToken cancellationToken)
        {
            if (definition is not BotDefinition)
            {
                return (ImmutableArray<WorkflowResponse>.Empty, null, ImmutableArray<SyncDataverseClient.AIPromptResponse>.Empty, ImmutableArray<SyncDataverseClient.AIPromptMetadata>.Empty);
            }

            var (workflowResponse, cloudFlowMetadata) = await synchronizer.UpsertWorkflowForAgentAsync(workspaceFolder, dataverseClient, agentId, cancellationToken, activationMode);
            var (aiPromptResponse, aiPromptMetadata) = await synchronizer.UpsertAIPromptsForAgentAsync(workspaceFolder, dataverseClient, agentId, cancellationToken);
            return (workflowResponse, cloudFlowMetadata, aiPromptResponse, aiPromptMetadata);
        }
    }
}
