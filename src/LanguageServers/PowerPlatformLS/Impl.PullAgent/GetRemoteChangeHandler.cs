namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.Agents.Platform.Content;
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.CopilotStudio.Sync;
    using Microsoft.CopilotStudio.Sync.Dataverse;
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.PowerPlatformLS.Contracts.FileLayout;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Impl.PullAgent.Auth;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.CopilotStudio.McsCore;


    [LanguageServerEndpoint("powerplatformls/getRemoteChanges", LanguageServerConstants.DefaultLanguageName)]
    internal class GetRemoteChangeHandler : IRequestHandler<SyncAgentRequest, SyncAgentResponse, RequestContext>
    {
        private readonly ITokenManager _dataverseTokenManager;
        private readonly CopilotStudio.Sync.IOperationContextProvider _operationContextProvider;
        private readonly ILspLogger _logger;
        private readonly CopilotStudio.Sync.IIslandControlPlaneService _islandControlPlaneService;
        protected readonly CopilotStudio.Sync.IWorkspaceSynchronizer _synchronizer;
        private readonly ISyncDataverseClient _dataverseClient;
        private readonly LspDataverseHttpClientAccessor _dataverseHttpClientAccessor;

        public GetRemoteChangeHandler(
            CopilotStudio.Sync.IIslandControlPlaneService islandControlPlaneService,
            CopilotStudio.Sync.IWorkspaceSynchronizer agentWriter,
            ITokenManager dataverseTokenManager,
            CopilotStudio.Sync.IOperationContextProvider operationContextProvider,
            ISyncDataverseClient dataverseClient,
            LspDataverseHttpClientAccessor dataverseHttpClientAccessor,
            ILspLogger logger)
        {
            _islandControlPlaneService = islandControlPlaneService;
            _synchronizer = agentWriter ?? throw new ArgumentNullException(nameof(agentWriter));
            _dataverseTokenManager = dataverseTokenManager ?? throw new ArgumentNullException(nameof(dataverseTokenManager));
            _operationContextProvider = operationContextProvider ?? throw new ArgumentNullException(nameof(operationContextProvider));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _dataverseClient = dataverseClient ?? throw new ArgumentNullException(nameof(dataverseClient));
            _dataverseHttpClientAccessor = dataverseHttpClientAccessor ?? throw new ArgumentNullException(nameof(dataverseHttpClientAccessor));
        }

        public bool MutatesSolutionState => false;

        public async Task<SyncAgentResponse> HandleRequestAsync(SyncAgentRequest request, RequestContext context, CancellationToken cancellationToken)
        {
            try
            {
                _islandControlPlaneService.SetConnectionContext(
                    request.EnvironmentInfo.AgentManagementUrl,
                    request.AccountInfo.ClusterCategory);
                _dataverseTokenManager.SetTokens(request.DataverseAccessToken, request.CopilotStudioAccessToken);
                _dataverseHttpClientAccessor.SetDataverseUrl(new Uri(request.EnvironmentInfo.DataverseUrl));
                _dataverseClient.SetDataverseUrl(request.EnvironmentInfo.DataverseUrl);

                var workspace = (IMcsWorkspace)context.Workspace;
                var syncInfo = await _synchronizer.GetSyncInfoAsync(workspace.FolderPath);
                var operationContext = await _operationContextProvider.GetAsync(syncInfo);
                var (_, localChanges) = await _synchronizer.GetRemoteChangesAsync(workspace.FolderPath, operationContext, _dataverseClient, syncInfo.AgentId, cancellationToken);

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