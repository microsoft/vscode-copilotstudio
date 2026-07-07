namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.Agents.Platform.Content;
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.CopilotStudio.Sync;
    using Microsoft.CopilotStudio.Sync.Dataverse;
    using Microsoft.PowerPlatformLS.Contracts.FileLayout;
    using Microsoft.PowerPlatformLS.Impl.PullAgent.Auth;
    using System.Collections.Immutable;
    using System.Threading;
    using System.Threading.Tasks;


    [LanguageServerEndpoint("powerplatformls/syncPush", LanguageServerConstants.DefaultLanguageName)]
    internal class SyncPushHandler : SyncHandler
    {
        public SyncPushHandler(CopilotStudio.Sync.IIslandControlPlaneService islandControlPlaneService, CopilotStudio.Sync.IWorkspaceSynchronizer workspaceSynchronizer, ITokenManager dataverseTokenManager, ISyncDataverseClient dataverseClient, LspDataverseHttpClientAccessor dataverseHttpClientAccessor, CopilotStudio.Sync.IOperationContextProvider operationContextProvider, ILspLogger logger)
            : base(islandControlPlaneService, workspaceSynchronizer, dataverseTokenManager, dataverseClient, dataverseHttpClientAccessor, operationContextProvider, logger)
        {
        }

        protected override async Task<(DefinitionBase, ImmutableArray<WorkflowResponse>, ImmutableArray<SyncDataverseClient.AIPromptResponse>)> ExecuteAsync(SyncAgentRequest request, IMcsWorkspace workspace, AuthoringOperationContextBase operationContext, ISyncDataverseClient dataverseClient, AgentSyncInfo syncInfo, CancellationToken cancellationToken)
        {
            // Fail-closed support gate (TDD D35): push is destructive to the cloud, so it
            // requires a Supported authoring shape. Classify from the definition AND the
            // workspace layout: a plain classic agent resolves to Supported via its layout, a
            // component collection is a recognized format, but an explicitly unrecognized shape
            // stays Provisional and is blocked. EnsureAllowed throws InvalidOperationException
            // (-> 400 user error) when blocked.
            var classification = AgentClassifier.Classify(workspace.Definition, workspace.FolderPath.ToString());
            AuthoringSupportGate.EnsureAllowed(classification, SyncOperation.Push);

            await ConnectionHelper.ProvisionConnectionsAsync(_synchronizer, workspace.FolderPath, workspace.Definition, dataverseClient, cancellationToken);

            var activationMode = request.DraftConnectionReferenceWorkflows ? CopilotStudio.Sync.WorkflowActivationMode.DraftWhenConnectionReferencesExist : CopilotStudio.Sync.WorkflowActivationMode.DraftWhenConnectionsUnbound;
            var (workflowResponse, cloudFlowMetadata, aiPromptResponse, aiPromptMetadata) = operationContext is BotComponentCollectionAuthoringOperationContext
                ? await UpsertComponentCollectionScopedAssetsAsync(workspace.FolderPath, dataverseClient, syncInfo.ComponentCollectionId, activationMode, cancellationToken)
                : await ConnectionHelper.UpsertAgentScopedAssetsAsync(_synchronizer, workspace.FolderPath, workspace.Definition, dataverseClient, syncInfo.AgentId, activationMode, cancellationToken);

            var contentSaveContextOverride = await TryBuildComponentCollectionHostContextAsync(workspace, operationContext, dataverseClient, syncInfo, cancellationToken);

            await _synchronizer.PushLocalChangesAsync(workspace.FolderPath, operationContext, workspace.Definition, dataverseClient, syncInfo, cloudFlowMetadata, aiPromptMetadata, cancellationToken, contentSaveContextOverride);
            return (workspace.Definition, workflowResponse, aiPromptResponse);
        }

        private async Task<(ImmutableArray<WorkflowResponse>, CloudFlowMetadata?, ImmutableArray<SyncDataverseClient.AIPromptResponse>, ImmutableArray<SyncDataverseClient.AIPromptMetadata>)> UpsertComponentCollectionScopedAssetsAsync(DirectoryPath workspaceFolder, ISyncDataverseClient dataverseClient, Guid? componentCollectionId, CopilotStudio.Sync.WorkflowActivationMode activationMode, CancellationToken cancellationToken)
        {
            var (workflowResponse, cloudFlowMetadata) = await _synchronizer.UpsertWorkflowForAgentAsync(workspaceFolder, dataverseClient, componentCollectionId, cancellationToken, activationMode);
            var (aiPromptResponse, aiPromptMetadata) = await _synchronizer.UpsertAIPromptsForAgentAsync(workspaceFolder, dataverseClient, componentCollectionId, cancellationToken);
            return (workflowResponse, cloudFlowMetadata, aiPromptResponse, aiPromptMetadata);
        }

        private async Task<AuthoringOperationContextBase?> TryBuildComponentCollectionHostContextAsync(IMcsWorkspace workspace, AuthoringOperationContextBase operationContext, ISyncDataverseClient dataverseClient, AgentSyncInfo syncInfo, CancellationToken cancellationToken)
        {
            if (operationContext is not BotComponentCollectionAuthoringOperationContext || syncInfo.ComponentCollectionId is not Guid componentCollectionId)
            {
                return null;
            }

            if (!workspace.Definition.Components.Any(ComponentReferencesWorkflowOrModel))
            {
                return null;
            }

            if (dataverseClient is not ISyncComponentCollectionDataverseClient componentCollectionDataverseClient)
            {
                return null;
            }

            var hostAgentIds = await componentCollectionDataverseClient.GetAgentIdsForComponentCollectionAsync(componentCollectionId, cancellationToken);
            if (hostAgentIds.Count == 0)
            {
                return null;
            }

            var hostSyncInfo = new AgentSyncInfo
            {
                DataverseEndpoint = syncInfo.DataverseEndpoint,
                EnvironmentId = syncInfo.EnvironmentId,
                EnvironmentDisplayName = syncInfo.EnvironmentDisplayName,
                AccountInfo = syncInfo.AccountInfo,
                AgentId = hostAgentIds[0],
                ComponentCollectionId = null,
                SolutionVersions = syncInfo.SolutionVersions,
                AgentManagementEndpoint = syncInfo.AgentManagementEndpoint,
            };

            return await _operationContextProvider.GetAsync(hostSyncInfo);
        }

        internal static bool ComponentReferencesWorkflowOrModel(BotComponentBase component)
        {
            if (component.RootElement is null)
            {
                return false;
            }

            var serialized = CodeSerializer.Serialize(component.RootElement);
            return serialized.IndexOf("InvokeFlowTaskAction", StringComparison.OrdinalIgnoreCase) >= 0
                || serialized.IndexOf("flowId", StringComparison.OrdinalIgnoreCase) >= 0
                || serialized.IndexOf("InvokeAIBuilderModelTaskAction", StringComparison.OrdinalIgnoreCase) >= 0
                || serialized.IndexOf("aIModelId", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
