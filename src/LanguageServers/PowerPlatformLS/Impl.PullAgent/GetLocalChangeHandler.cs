namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.CopilotStudio.Sync;
    using Microsoft.CopilotStudio.Sync.Dataverse;
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.PowerPlatformLS.Contracts.FileLayout;
    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Impl.PullAgent.Auth;
    using System.Threading;
    using System.Threading.Tasks;


    [LanguageServerEndpoint(Constants.JsonRpcMethods.GetLocalChanges, LanguageServerConstants.DefaultLanguageName)]
    internal class GetLocalChangeHandler : IRequestHandler<DiffLocalRequest, SyncAgentResponse, RequestContext>
    {
        private readonly CopilotStudio.Sync.IWorkspaceSynchronizer _workspaceSynchronizer;
        private readonly ISyncDataverseClient _dataverseClient;
        private readonly LspDataverseHttpClientAccessor _dataverseHttpClientAccessor;
        private readonly ITokenManager _dataverseTokenManager;
        private readonly ILspLogger _logger;

        public GetLocalChangeHandler(
            CopilotStudio.Sync.IWorkspaceSynchronizer workspaceSynchronizer,
            ISyncDataverseClient dataverseClient,
            LspDataverseHttpClientAccessor dataverseHttpClientAccessor,
            ITokenManager dataverseTokenManager,
            ILspLogger logger)
        {
            _workspaceSynchronizer = workspaceSynchronizer;
            _dataverseClient = dataverseClient ?? throw new ArgumentNullException(nameof(dataverseClient));
            _dataverseHttpClientAccessor = dataverseHttpClientAccessor ?? throw new ArgumentNullException(nameof(dataverseHttpClientAccessor));
            _dataverseTokenManager = dataverseTokenManager ?? throw new ArgumentNullException(nameof(dataverseTokenManager));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public bool MutatesSolutionState => false;

        public async Task<SyncAgentResponse> HandleRequestAsync(DiffLocalRequest request, RequestContext context, CancellationToken cancellationToken)
        {
            try
            {
                _dataverseTokenManager.SetTokens(request.DataverseAccessToken, request.CopilotStudioAccessToken);
                _dataverseHttpClientAccessor.SetDataverseUrl(new Uri(request.EnvironmentInfo.DataverseUrl));
                _dataverseClient.SetDataverseUrl(request.EnvironmentInfo.DataverseUrl);

                var workspace = (IMcsWorkspace)context.Workspace;
                var syncInfo = await _workspaceSynchronizer.GetSyncInfoAsync(workspace.FolderPath);
                var (_, localChanges) = await _workspaceSynchronizer.GetLocalChangesAsync(workspace.FolderPath, workspace.Definition, _dataverseClient, syncInfo, cancellationToken);

                return new SyncAgentResponse
                {
                    Code = 200,
                    Message = string.Empty,
                    LocalChanges = localChanges
                };
            }
            catch (Exception ex)
            {
                var (code, message) = LspExceptionHandler.Handle(ex, _logger, cancellationToken);
                return new SyncAgentResponse
                {
                    Code = code,
                    Message = message,
                };
            }
        }
    }
}
