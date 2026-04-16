namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.Agents.Platform.Content;
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.CopilotStudio.Sync;
    using Microsoft.CopilotStudio.Sync.Dataverse;
    using Microsoft.CopilotStudio.McsCore;
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

        protected override async Task<(DefinitionBase, ImmutableArray<WorkflowResponse>)> ExecuteAsync(IMcsWorkspace workspace, AuthoringOperationContextBase operationContext, ISyncDataverseClient dataverseClient, Guid? agentId, CancellationToken cancellationToken)
        {
            var (workflowResponse, cloudFlowMetadata) = await _synchronizer.UpsertWorkflowForAgentAsync(workspace.FolderPath, dataverseClient, agentId, cancellationToken);

            await _synchronizer.ProvisionConnectionReferencesAsync(workspace.Definition, dataverseClient, cancellationToken);

            // Execute the push
            var (localChanges, changeList) = await _synchronizer.GetLocalChangesAsync(workspace.FolderPath, workspace.Definition, dataverseClient, agentId, cancellationToken);
            if (!changeList.Any(c => c.SchemaName == "entity" || c.SchemaName == "icon"))
            {
                localChanges = localChanges.WithBot(null);
            }

            await _synchronizer.PushChangesetAsync(workspace.FolderPath, operationContext, localChanges, dataverseClient, agentId, cloudFlowMetadata, cancellationToken);
            return (workspace.Definition, workflowResponse);
        }
    }
}
