namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.CopilotStudio.Sync;
    using Microsoft.CopilotStudio.Sync.Dataverse;
    using Microsoft.PowerPlatformLS.Contracts.FileLayout;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Impl.PullAgent.Auth;
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    [LanguageServerEndpoint("powerplatformls/downloadKnowledgeFiles", LanguageServerConstants.DefaultLanguageName)]
    internal class DownloadKnowledgeFilesHandler : IRequestHandler<DownloadKnowledgeFilesRequest, DownloadKnowledgeFilesResponse, RequestContext>
    {
        private readonly IIslandControlPlaneService _islandControlPlaneService;
        private readonly IWorkspaceSynchronizer _synchronizer;
        private readonly ITokenManager _dataverseTokenManager;
        private readonly ISyncDataverseClient _dataverseClient;
        private readonly LspDataverseHttpClientAccessor _dataverseHttpClientAccessor;
        private readonly ILspLogger _logger;

        public DownloadKnowledgeFilesHandler(
            IIslandControlPlaneService islandControlPlaneService,
            IWorkspaceSynchronizer synchronizer,
            ITokenManager dataverseTokenManager,
            ISyncDataverseClient dataverseClient,
            LspDataverseHttpClientAccessor dataverseHttpClientAccessor,
            ILspLogger logger)
        {
            _islandControlPlaneService = islandControlPlaneService ?? throw new ArgumentNullException(nameof(islandControlPlaneService));
            _synchronizer = synchronizer ?? throw new ArgumentNullException(nameof(synchronizer));
            _dataverseTokenManager = dataverseTokenManager ?? throw new ArgumentNullException(nameof(dataverseTokenManager));
            _dataverseClient = dataverseClient ?? throw new ArgumentNullException(nameof(dataverseClient));
            _dataverseHttpClientAccessor = dataverseHttpClientAccessor ?? throw new ArgumentNullException(nameof(dataverseHttpClientAccessor));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public bool MutatesSolutionState => true;

        public async Task<DownloadKnowledgeFilesResponse> HandleRequestAsync(DownloadKnowledgeFilesRequest request, RequestContext context, CancellationToken cancellationToken)
        {
            try
            {
                ConnectionHelper.ApplyConnectionContext(_islandControlPlaneService, _dataverseTokenManager, _dataverseHttpClientAccessor, _dataverseClient, request);

                var workspace = (IMcsWorkspace)context.Workspace;
                var downloaded = await _synchronizer.DownloadKnowledgeFilesAsync(workspace.FolderPath, _dataverseClient, request.SchemaNames, cancellationToken);

                return new DownloadKnowledgeFilesResponse
                {
                    Code = 200,
                    Downloaded = downloaded,
                };
            }
            catch (Exception ex)
            {
                var (code, message) = LspExceptionHandler.Handle(ex, _logger, cancellationToken);
                return new DownloadKnowledgeFilesResponse
                {
                    Code = code,
                    Message = message,
                };
            }
        }
    }
}
