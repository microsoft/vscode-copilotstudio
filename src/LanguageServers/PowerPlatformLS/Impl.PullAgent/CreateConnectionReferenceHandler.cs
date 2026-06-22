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
    using System.Collections.Immutable;
    using System.Threading;
    using System.Threading.Tasks;

    [LanguageServerEndpoint(CreateConnectionReferenceRequest.MessageName, LanguageServerConstants.DefaultLanguageName)]
    internal class CreateConnectionReferenceHandler : IRequestHandler<CreateConnectionReferenceRequest, CreateConnectionReferenceResponse, RequestContext>
    {
        private readonly IIslandControlPlaneService _islandControlPlaneService;
        private readonly IConnectionManagementService _connectionManagementService;
        private readonly ITokenManager _dataverseTokenManager;
        private readonly ISyncDataverseClient _dataverseClient;
        private readonly IConnectionCatalogClient _connectionCatalogClient;
        private readonly LspDataverseHttpClientAccessor _dataverseHttpClientAccessor;
        private readonly ILspLogger _logger;

        public bool MutatesSolutionState => true;

        public CreateConnectionReferenceHandler(
            IIslandControlPlaneService islandControlPlaneService,
            IConnectionManagementService connectionManagementService,
            ITokenManager dataverseTokenManager,
            ISyncDataverseClient dataverseClient,
            IConnectionCatalogClient connectionCatalogClient,
            LspDataverseHttpClientAccessor dataverseHttpClientAccessor,
            ILspLogger logger)
        {
            _islandControlPlaneService = islandControlPlaneService;
            _connectionManagementService = connectionManagementService ?? throw new ArgumentNullException(nameof(connectionManagementService));
            _dataverseTokenManager = dataverseTokenManager ?? throw new ArgumentNullException(nameof(dataverseTokenManager));
            _dataverseClient = dataverseClient ?? throw new ArgumentNullException(nameof(dataverseClient));
            _connectionCatalogClient = connectionCatalogClient ?? throw new ArgumentNullException(nameof(connectionCatalogClient));
            _dataverseHttpClientAccessor = dataverseHttpClientAccessor ?? throw new ArgumentNullException(nameof(dataverseHttpClientAccessor));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<CreateConnectionReferenceResponse> HandleRequestAsync(CreateConnectionReferenceRequest request, RequestContext context, CancellationToken cancellationToken)
        {
            try
            {
                ConnectionHelper.ApplyConnectionContext(_islandControlPlaneService, _dataverseTokenManager, _dataverseHttpClientAccessor, _dataverseClient, request);
                var workspace = (IMcsWorkspace)context.Workspace;
                var classification = AgentClassifier.Classify(workspace.Definition, workspace.FolderPath.ToString());

                if (!classification.Allows(SyncOperation.Push))
                {
                    return new CreateConnectionReferenceResponse()
                    {
                        Code = 400,
                        Message = AuthoringSupportGate.DescribeBlocked(classification, SyncOperation.Push),
                    };
                }

                var logicalName = await _connectionManagementService.CreateConnectionReferenceForConnectorAsync(
                    workspace.FolderPath,
                    workspace.Definition,
                    request.ConnectorInternalId,
                    _dataverseClient,
                    cancellationToken);

                var catalogContext = ConnectionHelper.BuildCatalogContext(request, request.ConnectionsAccessToken);
                var views = await _connectionManagementService.GetAgentConnectionViewsAsync(
                    workspace.FolderPath,
                    workspace.Definition,
                    _dataverseClient,
                    _connectionCatalogClient,
                    catalogContext,
                    cancellationToken);

                _connectionManagementService.WriteConnectionsCache(workspace.FolderPath, views);

                return new CreateConnectionReferenceResponse()
                {
                    Code = 200,
                    Message = string.Empty,
                    LogicalName = logicalName,
                    AgentConnections = views.ToImmutableArray(),
                };
            }
            catch (DataverseBadRequestException ex)
            {
                _logger.LogException(ex);
                return new CreateConnectionReferenceResponse() { Code = ex.StatusCode, Message = ex.Message };
            }
            catch (Exception ex)
            {
                var (code, message) = LspExceptionHandler.Handle(ex, _logger, cancellationToken);
                return new CreateConnectionReferenceResponse() { Code = code, Message = message };
            }
        }
    }
}
