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

    internal abstract class SyncHandler : IRequestHandler<SyncAgentRequest, SyncAgentResponse, RequestContext>
    {
        private readonly ITokenManager _dataverseTokenManager;
        private readonly IOperationContextProvider _operationContextProvider;
        protected readonly ILspLogger _logger;
        private readonly IIslandControlPlaneService _islandControlPlaneService;
        protected readonly IWorkspaceSynchronizer _synchronizer;
        private readonly Func<string, string, DataverseClient> _dataverseClientFactory;

        protected SyncHandler(
            IIslandControlPlaneService islandControlPlaneService,
            IWorkspaceSynchronizer agentWriter,
            ITokenManager tokenManager,
            Func<string, string, DataverseClient> dataverseClientFactory,
            IOperationContextProvider operationContextProvider,
            ILspLogger logger)
        {
            _islandControlPlaneService = islandControlPlaneService;
            _synchronizer = agentWriter ?? throw new ArgumentNullException(nameof(agentWriter));
            _dataverseTokenManager = tokenManager ?? throw new ArgumentNullException(nameof(tokenManager));
            _operationContextProvider = operationContextProvider ?? throw new ArgumentNullException(nameof(operationContextProvider));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _dataverseClientFactory = dataverseClientFactory ?? throw new ArgumentNullException(nameof(dataverseClientFactory));
        }

        public bool MutatesSolutionState => true;

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
                var updatedDefinition = await ExecuteAsync(workspace, operationContext, dataverseClient, syncInfo.AgentId, cancellationToken);
                var (_, localChanges) = await _synchronizer.GetLocalChangesAsync(workspace.FolderPath, updatedDefinition, cancellationToken);

                return new SyncAgentResponse
                {
                    Code = 200,
                    Message = string.Empty,
                    LocalChanges = localChanges
                };
            }
            catch (InvalidOperationException ex)
            {
                // User errors 
                return new SyncAgentResponse
                {
                    Code = 400,
                    Message = ex.Message,
                };
            }
            catch (Exception ex)
            {
                _logger.LogException(ex);
                return new SyncAgentResponse
                {
                    Code = 500,
                    Message = ex.Message,
                };
            }
        }

        protected abstract Task<DefinitionBase> ExecuteAsync(
            IMcsWorkspace workspace,
            AuthoringOperationContextBase operationContext,
            DataverseClient dataverseClient,
            Guid? agentId,
            CancellationToken cancellationToken);
    }
}
