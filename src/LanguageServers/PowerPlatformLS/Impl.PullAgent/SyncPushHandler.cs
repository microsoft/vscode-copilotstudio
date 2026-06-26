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
            var (workflowResponse, cloudFlowMetadata) = await _synchronizer.UpsertWorkflowForAgentAsync(workspace.FolderPath, dataverseClient, syncInfo.AgentId, cancellationToken, activationMode);

            var (aiPromptResponse, aiPromptMetadata) = await _synchronizer.UpsertAIPromptsForAgentAsync(workspace.FolderPath, dataverseClient, syncInfo.AgentId, cancellationToken);

            await _synchronizer.PushLocalChangesAsync(workspace.FolderPath, operationContext, workspace.Definition, dataverseClient, syncInfo, cloudFlowMetadata, aiPromptMetadata, cancellationToken);
            return (workspace.Definition, workflowResponse, aiPromptResponse);
        }
    }
}
