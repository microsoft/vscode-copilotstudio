namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.CopilotStudio.Sync;
    using Microsoft.CopilotStudio.Sync.Dataverse;
    using Microsoft.PowerPlatformLS.Impl.PullAgent.Auth;
    using System;
    using System.Collections.Immutable;
    using System.Threading;
    using System.Threading.Tasks;

    internal static class ConnectionHelper
    {
        public static void ApplyConnectionContext(IIslandControlPlaneService islandControlPlaneService, ITokenManager dataverseTokenManager, LspDataverseHttpClientAccessor dataverseHttpClientAccessor, ISyncDataverseClient dataverseClient, DataverseRequest request)
        {
            islandControlPlaneService.SetConnectionContext(request.EnvironmentInfo.AgentManagementUrl, request.AccountInfo.ClusterCategory);
            dataverseTokenManager.SetTokens(request.DataverseAccessToken, request.CopilotStudioAccessToken);
            dataverseHttpClientAccessor.SetDataverseUrl(new Uri(request.EnvironmentInfo.DataverseUrl));
            dataverseClient.SetDataverseUrl(request.EnvironmentInfo.DataverseUrl);
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
