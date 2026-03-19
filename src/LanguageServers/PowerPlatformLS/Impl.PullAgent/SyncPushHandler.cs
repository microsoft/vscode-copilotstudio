namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.Agents.Platform.Content;
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.PowerPlatformLS.Contracts.FileLayout;
    using Microsoft.PowerPlatformLS.Impl.PullAgent.Auth;
    using Microsoft.PowerPlatformLS.Impl.PullAgent.Dataverse;
    using System;
    using System.Collections.Immutable;
    using System.Linq;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;


    [LanguageServerEndpoint("powerplatformls/syncPush", LanguageServerConstants.DefaultLanguageName)]
    internal class SyncPushHandler : SyncHandler
    {
        public SyncPushHandler(IIslandControlPlaneService islandControlPlaneService, IWorkspaceSynchronizer workspaceSynchronizer, ITokenManager dataverseTokenManager, Func<string, string, DataverseClient> dataverseClientFactory, IOperationContextProvider operationContextProvider, ILspLogger logger)
            : base(islandControlPlaneService, workspaceSynchronizer, dataverseTokenManager, dataverseClientFactory, operationContextProvider, logger)
        {
        }

        protected override async Task<(DefinitionBase, ImmutableArray<WorkflowResponse>)> ExecuteAsync(IMcsWorkspace workspace, AuthoringOperationContextBase operationContext, DataverseClient dataverseClient, Guid? agentId, CancellationToken cancellationToken)
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