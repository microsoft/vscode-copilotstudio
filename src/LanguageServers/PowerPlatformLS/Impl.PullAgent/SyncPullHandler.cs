namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.Agents.Platform.Content;
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.PowerPlatformLS.Contracts.FileLayout;
    using Microsoft.PowerPlatformLS.Impl.PullAgent.Auth;
    using Microsoft.PowerPlatformLS.Impl.PullAgent.Dataverse;
    using System.Collections.Immutable;
    using System.Threading;
    using System.Threading.Tasks;

    [LanguageServerEndpoint("powerplatformls/syncPull", LanguageServerConstants.DefaultLanguageName)]
    internal class SyncPullHandler : SyncHandler
    {
        public SyncPullHandler(IIslandControlPlaneService islandControlPlaneService, IWorkspaceSynchronizer workspaceSynchronizer, ITokenManager dataverseTokenManager, Func<string, string, DataverseClient> dataverseClientFactory, IOperationContextProvider operationContextProvider, ILspLogger logger)
            : base(islandControlPlaneService, workspaceSynchronizer, dataverseTokenManager, dataverseClientFactory, operationContextProvider, logger)
        {
        }

        protected override async Task<(DefinitionBase, ImmutableArray<WorkflowResponse>)> ExecuteAsync(IMcsWorkspace workspace, AuthoringOperationContextBase operationContext, DataverseClient dataverseClient, Guid? agentId, CancellationToken cancellationToken)
        {
            return (await _synchronizer.PullExistingChangesAsync(workspace.FolderPath, operationContext, workspace.Definition, dataverseClient, agentId, cancellationToken).ConfigureAwait(false), ImmutableArray<WorkflowResponse>.Empty);
        }
    }
}
