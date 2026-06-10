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
    using System;
    using System.Collections.Immutable;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;


    [LanguageServerEndpoint("powerplatformls/syncPush", LanguageServerConstants.DefaultLanguageName)]
    internal class SyncPushHandler : SyncHandler
    {
        public SyncPushHandler(CopilotStudio.Sync.IIslandControlPlaneService islandControlPlaneService, CopilotStudio.Sync.IWorkspaceSynchronizer workspaceSynchronizer, ITokenManager dataverseTokenManager, ISyncDataverseClient dataverseClient, LspDataverseHttpClientAccessor dataverseHttpClientAccessor, CopilotStudio.Sync.IOperationContextProvider operationContextProvider, ILspLogger logger)
            : base(islandControlPlaneService, workspaceSynchronizer, dataverseTokenManager, dataverseClient, dataverseHttpClientAccessor, operationContextProvider, logger)
        {
        }

        protected override async Task<(DefinitionBase, ImmutableArray<WorkflowResponse>, ImmutableArray<SyncDataverseClient.AIPromptResponse>, ImmutableArray<string>)> ExecuteAsync(IMcsWorkspace workspace, AuthoringOperationContextBase operationContext, ISyncDataverseClient dataverseClient, AgentSyncInfo syncInfo, CancellationToken cancellationToken)
        {
            // Fail-closed support gate (TDD D35): push is destructive to the cloud, so it
            // requires a Supported authoring shape. Classify from the definition AND the
            // workspace layout: a plain classic agent resolves to Supported via its layout, a
            // component collection is a recognized format, but an explicitly unrecognized shape
            // stays Provisional and is blocked. EnsureAllowed throws InvalidOperationException
            // (-> 400 user error) when blocked.
            var classification = AgentClassifier.Classify(workspace.Definition, workspace.FolderPath.ToString());
            AuthoringSupportGate.EnsureAllowed(classification, SyncOperation.Push);

            var (workflowResponse, cloudFlowMetadata) = await _synchronizer.UpsertWorkflowForAgentAsync(workspace.FolderPath, dataverseClient, syncInfo.AgentId, cancellationToken);

            var (aiPromptResponse, aiPromptMetadata) = await _synchronizer.UpsertAIPromptsForAgentAsync(workspace.FolderPath, dataverseClient, syncInfo.AgentId, cancellationToken);

            await _synchronizer.ProvisionConnectionReferencesAsync(workspace.FolderPath, workspace.Definition, dataverseClient, cancellationToken);

            // Execute the push
            var (localChanges, changeList) = await _synchronizer.GetLocalChangesAsync(workspace.FolderPath, workspace.Definition, dataverseClient, syncInfo, cancellationToken);
            if (!changeList.Any(c => c.SchemaName == "entity" || c.SchemaName == "icon"))
            {
                localChanges = localChanges.WithBot(null);
            }

            var pushResult = await _synchronizer.PushChangesetAsync(workspace.FolderPath, operationContext, localChanges, dataverseClient, syncInfo.AgentId, cloudFlowMetadata, aiPromptMetadata, cancellationToken);
            return (workspace.Definition, workflowResponse, aiPromptResponse, pushResult.NewlyCreatedCustomConnectors.ToImmutableArray());
        }
    }
}
