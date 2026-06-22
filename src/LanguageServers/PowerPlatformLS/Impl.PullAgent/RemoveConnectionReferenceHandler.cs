namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.Agents.Platform.Content.Exceptions;
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.CopilotStudio.Sync;
    using Microsoft.CopilotStudio.Sync.Dataverse;
    using Microsoft.PowerPlatformLS.Contracts.FileLayout;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Impl.PullAgent.Auth;
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    [LanguageServerEndpoint(RemoveConnectionReferenceRequest.MessageName, LanguageServerConstants.DefaultLanguageName)]
    internal class RemoveConnectionReferenceHandler : IRequestHandler<RemoveConnectionReferenceRequest, RemoveConnectionReferenceResponse, RequestContext>
    {
        private readonly IIslandControlPlaneService _islandControlPlaneService;
        private readonly IConnectionManagementService _connectionManagementService;
        private readonly ITokenManager _dataverseTokenManager;
        private readonly ISyncDataverseClient _dataverseClient;
        private readonly LspDataverseHttpClientAccessor _dataverseHttpClientAccessor;
        private readonly ILspLogger _logger;

        public bool MutatesSolutionState => true;

        public RemoveConnectionReferenceHandler(
            IIslandControlPlaneService islandControlPlaneService,
            IConnectionManagementService connectionManagementService,
            ITokenManager dataverseTokenManager,
            ISyncDataverseClient dataverseClient,
            LspDataverseHttpClientAccessor dataverseHttpClientAccessor,
            ILspLogger logger)
        {
            _islandControlPlaneService = islandControlPlaneService;
            _connectionManagementService = connectionManagementService ?? throw new ArgumentNullException(nameof(connectionManagementService));
            _dataverseTokenManager = dataverseTokenManager ?? throw new ArgumentNullException(nameof(dataverseTokenManager));
            _dataverseClient = dataverseClient ?? throw new ArgumentNullException(nameof(dataverseClient));
            _dataverseHttpClientAccessor = dataverseHttpClientAccessor ?? throw new ArgumentNullException(nameof(dataverseHttpClientAccessor));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<RemoveConnectionReferenceResponse> HandleRequestAsync(RemoveConnectionReferenceRequest request, RequestContext context, CancellationToken cancellationToken)
        {
            try
            {
                ConnectionHelper.ApplyConnectionContext(_islandControlPlaneService, _dataverseTokenManager, _dataverseHttpClientAccessor, _dataverseClient, request);
                var workspace = (IMcsWorkspace)context.Workspace;
                var classification = AgentClassifier.Classify(workspace.Definition, workspace.FolderPath.ToString());

                if (!classification.Allows(SyncOperation.Push))
                {
                    return new RemoveConnectionReferenceResponse()
                    {
                        Code = 400,
                        Message = AuthoringSupportGate.DescribeBlocked(classification, SyncOperation.Push),
                    };
                }

                var result = await _connectionManagementService.RemoveConnectionReferenceAsync(
                    workspace.FolderPath,
                    workspace.Definition,
                    request.LogicalName,
                    request.Confirmed,
                    cancellationToken);

                return new RemoveConnectionReferenceResponse()
                {
                    Code = 200,
                    Message = string.Empty,
                    Removed = result.Removed,
                    Usages = result.Usages,
                };
            }
            catch (DataverseBadRequestException ex)
            {
                _logger.LogException(ex);
                return new RemoveConnectionReferenceResponse() { Code = ex.StatusCode, Message = ex.Message };
            }
            catch (Exception ex)
            {
                var (code, message) = LspExceptionHandler.Handle(ex, _logger, cancellationToken);
                return new RemoveConnectionReferenceResponse() { Code = code, Message = message };
            }
        }
    }
}
