namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.Agents.Platform.Content.Exceptions;
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.CopilotStudio.Sync;
    using Microsoft.CopilotStudio.Sync.Dataverse;
    using Microsoft.PowerPlatformLS.Contracts.FileLayout;
    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Impl.PullAgent.Auth;
    using System;
    using System.Collections.Immutable;
    using System.Threading;
    using System.Threading.Tasks;

    [LanguageServerEndpoint(ApplyConnectionBindingsRequest.MessageName, LanguageServerConstants.DefaultLanguageName)]
    internal class ApplyConnectionBindingsHandler : IRequestHandler<ApplyConnectionBindingsRequest, ApplyConnectionBindingsResponse, RequestContext>
    {
        private readonly IIslandControlPlaneService _islandControlPlaneService;
        private readonly IWorkspaceSynchronizer _workspaceSynchronizer;
        private readonly ITokenManager _dataverseTokenManager;
        private readonly ISyncDataverseClient _dataverseClient;
        private readonly IConnectionCatalogClient _connectionCatalogClient;
        private readonly LspDataverseHttpClientAccessor _dataverseHttpClientAccessor;
        private readonly IDiagnosticsPublisher _diagnosticsPublisher;
        private readonly ILspLogger _logger;

        public bool MutatesSolutionState => true;

        public ApplyConnectionBindingsHandler(
            IIslandControlPlaneService islandControlPlaneService,
            IWorkspaceSynchronizer workspaceSynchronizer,
            ITokenManager dataverseTokenManager,
            ISyncDataverseClient dataverseClient,
            IConnectionCatalogClient connectionCatalogClient,
            LspDataverseHttpClientAccessor dataverseHttpClientAccessor,
            IDiagnosticsPublisher diagnosticsPublisher,
            ILspLogger logger)
        {
            _islandControlPlaneService = islandControlPlaneService;
            _workspaceSynchronizer = workspaceSynchronizer ?? throw new ArgumentNullException(nameof(workspaceSynchronizer));
            _dataverseTokenManager = dataverseTokenManager ?? throw new ArgumentNullException(nameof(dataverseTokenManager));
            _dataverseClient = dataverseClient ?? throw new ArgumentNullException(nameof(dataverseClient));
            _connectionCatalogClient = connectionCatalogClient ?? throw new ArgumentNullException(nameof(connectionCatalogClient));
            _dataverseHttpClientAccessor = dataverseHttpClientAccessor ?? throw new ArgumentNullException(nameof(dataverseHttpClientAccessor));
            _diagnosticsPublisher = diagnosticsPublisher ?? throw new ArgumentNullException(nameof(diagnosticsPublisher));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<ApplyConnectionBindingsResponse> HandleRequestAsync(ApplyConnectionBindingsRequest request, RequestContext context, CancellationToken cancellationToken)
        {
            try
            {
                ConnectionHelper.ApplyConnectionContext(_islandControlPlaneService, _dataverseTokenManager, _dataverseHttpClientAccessor, _dataverseClient, request);
                var workspace = (IMcsWorkspace)context.Workspace;
                var classification = AgentClassifier.Classify(workspace.Definition, workspace.FolderPath.ToString());

                if (!classification.Allows(SyncOperation.Push))
                {
                    return new ApplyConnectionBindingsResponse()
                    {
                        Code = 400,
                        Message = AuthoringSupportGate.DescribeBlocked(classification, SyncOperation.Push),
                    };
                }

                var catalogContext = ConnectionHelper.BuildCatalogContext(request, request.ConnectionsAccessToken);
                var views = await _workspaceSynchronizer.ApplyConnectionBindingsAsync(
                    workspace.FolderPath,
                    workspace.Definition,
                    _dataverseClient,
                    _connectionCatalogClient,
                    catalogContext,
                    request.Bindings,
                    cancellationToken);

                await PublishConnectionDiagnosticsAsync(context, cancellationToken);

                return new ApplyConnectionBindingsResponse()
                {
                    Code = 200,
                    Message = string.Empty,
                    AgentConnections = views.ToImmutableArray(),
                };
            }
            catch (DataverseBadRequestException ex)
            {
                _logger.LogException(ex);
                return new ApplyConnectionBindingsResponse() { Code = ex.StatusCode, Message = ex.Message };
            }
            catch (Exception ex)
            {
                var (code, message) = LspExceptionHandler.Handle(ex, _logger, cancellationToken);
                return new ApplyConnectionBindingsResponse() { Code = code, Message = message };
            }
        }

        private async Task PublishConnectionDiagnosticsAsync(RequestContext context, CancellationToken cancellationToken)
        {
            try
            {
                await _diagnosticsPublisher.PublishAllDiagnosticsAsync(context, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogException(ex);
            }
        }
    }
}
