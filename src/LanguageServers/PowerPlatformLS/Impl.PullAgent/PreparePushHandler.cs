namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.Agents.Platform.Content.Exceptions;
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.CopilotStudio.Sync;
    using Microsoft.CopilotStudio.Sync.Dataverse;
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.PowerPlatformLS.Contracts.FileLayout;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Impl.PullAgent.Auth;
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Prepares connections for a push of an already-connected agent: provisions custom connectors and connection
    /// references, then returns the connection references the client must fulfill before <see cref="SyncAgentRequest"/>.
    /// </summary>
    [LanguageServerEndpoint(PreparePushRequest.MessageName, LanguageServerConstants.DefaultLanguageName)]
    internal class PreparePushHandler : IRequestHandler<PreparePushRequest, PreparePushResponse, RequestContext>
    {
        private readonly IIslandControlPlaneService _islandControlPlaneService;
        private readonly IWorkspaceSynchronizer _workspaceSynchronizer;
        private readonly ITokenManager _dataverseTokenManager;
        private readonly ISyncDataverseClient _dataverseClient;
        private readonly LspDataverseHttpClientAccessor _dataverseHttpClientAccessor;
        private readonly ILspLogger _logger;

        public bool MutatesSolutionState => true;

        public PreparePushHandler(
            IIslandControlPlaneService islandControlPlaneService,
            IWorkspaceSynchronizer workspaceSynchronizer,
            ITokenManager dataverseTokenManager,
            ISyncDataverseClient dataverseClient,
            LspDataverseHttpClientAccessor dataverseHttpClientAccessor,
            ILspLogger logger)
        {
            _islandControlPlaneService = islandControlPlaneService;
            _workspaceSynchronizer = workspaceSynchronizer ?? throw new ArgumentNullException(nameof(workspaceSynchronizer));
            _dataverseTokenManager = dataverseTokenManager ?? throw new ArgumentNullException(nameof(dataverseTokenManager));
            _dataverseClient = dataverseClient ?? throw new ArgumentNullException(nameof(dataverseClient));
            _dataverseHttpClientAccessor = dataverseHttpClientAccessor ?? throw new ArgumentNullException(nameof(dataverseHttpClientAccessor));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<PreparePushResponse> HandleRequestAsync(PreparePushRequest request, RequestContext context, CancellationToken cancellationToken)
        {
            try
            {
                ConnectionHelper.ApplyConnectionContext(_islandControlPlaneService, _dataverseTokenManager, _dataverseHttpClientAccessor, _dataverseClient, request);
                var workspace = (IMcsWorkspace)context.Workspace;
                var classification = AgentClassifier.Classify(workspace.Definition, workspace.FolderPath.ToString());

                if (!classification.Allows(SyncOperation.Push))
                {
                    return new PreparePushResponse()
                    {
                        Code = 400,
                        Message = AuthoringSupportGate.DescribeBlocked(classification, SyncOperation.Push),
                    };
                }

                var agentConnections = await ConnectionHelper.ProvisionAndGetConnectionsAsync(_workspaceSynchronizer, workspace.FolderPath, workspace.Definition, _dataverseClient, cancellationToken);

                return new PreparePushResponse()
                {
                    Code = 200,
                    Message = string.Empty,
                    AgentConnections = agentConnections,
                };
            }
            catch (DataverseBadRequestException ex)
            {
                _logger.LogException(ex);
                return new PreparePushResponse() { Code = ex.StatusCode, Message = ex.Message };
            }
            catch (Exception ex)
            {
                _logger.LogException(ex);
                return new PreparePushResponse() { Code = 500, Message = ex.Message };
            }
        }
    }
}
