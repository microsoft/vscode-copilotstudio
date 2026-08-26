namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.Agents.Platform.Content;
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.CopilotStudio.Sync;
    using Microsoft.CopilotStudio.Sync.Dataverse;
    using Microsoft.PowerPlatformLS.Contracts.FileLayout;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Impl.PullAgent.Auth;
    using System.Collections.Immutable;
    using System.Threading;
    using System.Threading.Tasks;

    [LanguageServerEndpoint("powerplatformls/syncPull", LanguageServerConstants.DefaultLanguageName)]
    internal class SyncPullHandler : SyncHandler
    {
        private readonly IClientWorkspaceFileProvider _fileProvider;

        public SyncPullHandler(CopilotStudio.Sync.IIslandControlPlaneService islandControlPlaneService, CopilotStudio.Sync.IWorkspaceSynchronizer workspaceSynchronizer, ITokenManager dataverseTokenManager, ISyncDataverseClient dataverseClient, LspDataverseHttpClientAccessor dataverseHttpClientAccessor, CopilotStudio.Sync.IOperationContextProvider operationContextProvider, IClientWorkspaceFileProvider fileProvider, ILspLogger logger)
            : base(islandControlPlaneService, workspaceSynchronizer, dataverseTokenManager, dataverseClient, dataverseHttpClientAccessor, operationContextProvider, logger)
        {
            _fileProvider = fileProvider;
        }

        protected override async Task<(DefinitionBase, ImmutableArray<WorkflowResponse>, ImmutableArray<SyncDataverseClient.AIPromptResponse>)> ExecuteAsync(SyncAgentRequest request, IMcsWorkspace workspace, AuthoringOperationContextBase operationContext, ISyncDataverseClient dataverseClient, AgentSyncInfo syncInfo, CancellationToken cancellationToken)
        {
            var updatedDefinition = await _synchronizer.PullExistingChangesAsync(workspace.FolderPath, operationContext, workspace.Definition, dataverseClient, syncInfo, cancellationToken).ConfigureAwait(false);
            if (workspace is Workspace trackedWorkspace)
            {
                trackedWorkspace.RemoveMissingDocuments(_fileProvider);
            }

            return (updatedDefinition, ImmutableArray<WorkflowResponse>.Empty, ImmutableArray<Microsoft.CopilotStudio.Sync.Dataverse.SyncDataverseClient.AIPromptResponse>.Empty);
        }
    }
}
