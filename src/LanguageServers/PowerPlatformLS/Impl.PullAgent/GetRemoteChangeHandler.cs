namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.Agents.Platform.Content;
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.PowerPlatformLS.Contracts.FileLayout;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Impl.PullAgent.Auth;
    using Microsoft.PowerPlatformLS.Impl.PullAgent.Dataverse;
    using System.Threading;
    using System.Threading.Tasks;


    [LanguageServerEndpoint("powerplatformls/getRemoteChanges", LanguageServerConstants.DefaultLanguageName)]
    internal class GetRemoteChangeHandler : IRequestHandler<SyncAgentRequest, SyncAgentResponse, RequestContext>
    {
        private readonly ITokenManager _dataverseTokenManager;
        private readonly IOperationContextProvider _operationContextProvider;
        private readonly ILspLogger _logger;
        private readonly IIslandControlPlaneService _islandControlPlaneService;
        protected readonly IWorkspaceSynchronizer _synchronizer;
        private readonly Func<string, string, DataverseClient> _dataverseClientFactory;

        public GetRemoteChangeHandler(
            IIslandControlPlaneService islandControlPlaneService,
            IWorkspaceSynchronizer agentWriter,
            ITokenManager dataverseTokenManager,
            IOperationContextProvider operationContextProvider,
            Func<string, string, DataverseClient> dataverseClientFactory,
            ILspLogger logger)
        {
            _islandControlPlaneService = islandControlPlaneService;
            _synchronizer = agentWriter ?? throw new ArgumentNullException(nameof(agentWriter));
            _dataverseTokenManager = dataverseTokenManager ?? throw new ArgumentNullException(nameof(dataverseTokenManager));
            _operationContextProvider = operationContextProvider ?? throw new ArgumentNullException(nameof(operationContextProvider));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _dataverseClientFactory = dataverseClientFactory ?? throw new ArgumentNullException(nameof(dataverseClientFactory));
        }

        public bool MutatesSolutionState => false;

        public async Task<SyncAgentResponse> HandleRequestAsync(SyncAgentRequest request, RequestContext context, CancellationToken cancellationToken)
        {
            try
            {
                _islandControlPlaneService.SetIslandBaseEndpoint(request.EnvironmentInfo.AgentManagementUrl);
                _dataverseTokenManager.SetTokens(request.DataverseAccessToken, request.CopilotStudioAccessToken);
                var workspace = (IMcsWorkspace)context.Workspace;
                var syncInfo = await _synchronizer.GetSyncInfoAsync(workspace.FolderPath);
                var operationContext = await _operationContextProvider.GetAsync(syncInfo);
                var dataverseClient = _dataverseClientFactory(request.EnvironmentInfo.DataverseUrl, request.DataverseAccessToken);
                var (_, localChanges) = await _synchronizer.GetRemoteChangesAsync(workspace.FolderPath, operationContext, dataverseClient, syncInfo.AgentId, cancellationToken);

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