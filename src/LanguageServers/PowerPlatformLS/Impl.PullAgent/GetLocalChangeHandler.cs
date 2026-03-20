namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.PowerPlatformLS.Contracts.FileLayout;
    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Impl.PullAgent.Dataverse;
    using System.Threading;
    using System.Threading.Tasks;


    [LanguageServerEndpoint(Constants.JsonRpcMethods.GetLocalChanges, LanguageServerConstants.DefaultLanguageName)]
    internal class GetLocalChangeHandler : IRequestHandler<DiffLocalRequest, SyncAgentResponse, RequestContext>
    {
        private readonly IWorkspaceSynchronizer _workspaceSynchronizer;
        private readonly Func<string, string, DataverseClient> _dataverseClientFactory;

        public GetLocalChangeHandler(IWorkspaceSynchronizer workspaceSynchronizer,
            Func<string, string, DataverseClient> dataverseClientFactory)
        {
            _workspaceSynchronizer = workspaceSynchronizer;
            _dataverseClientFactory = dataverseClientFactory ?? throw new ArgumentNullException(nameof(dataverseClientFactory));
        }

        public bool MutatesSolutionState => false;

        public async Task<SyncAgentResponse> HandleRequestAsync(DiffLocalRequest request, RequestContext context, CancellationToken cancellationToken)
        {
            try
            {
                var workspace = (IMcsWorkspace)context.Workspace;
                var dataverseClient = _dataverseClientFactory(request.EnvironmentInfo.DataverseUrl, request.DataverseAccessToken);
                var syncInfo = await _workspaceSynchronizer.GetSyncInfoAsync(workspace.FolderPath);
                var (_, localChanges) = await _workspaceSynchronizer.GetLocalChangesAsync(workspace.FolderPath, workspace.Definition, dataverseClient, syncInfo.AgentId, cancellationToken);

                return new SyncAgentResponse
                {
                    Code = 200,
                    Message = string.Empty,
                    LocalChanges = localChanges
                };
            }
            catch (Exception ex)
            {
                return new SyncAgentResponse
                {
                    Code = 500,
                    Message = ex.Message,
                };
            }
        }
    }
}