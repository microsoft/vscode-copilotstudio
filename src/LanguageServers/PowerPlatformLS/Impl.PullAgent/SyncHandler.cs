namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.Agents.Platform.Content;
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.CopilotStudio.Sync;
    using Microsoft.CopilotStudio.Sync.Dataverse;
    using Microsoft.PowerPlatformLS.Contracts.FileLayout;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Impl.PullAgent.Auth;
    using System.Collections.Immutable;
    using System.Threading;
    using System.Threading.Tasks;
    using DirectoryPath = Microsoft.PowerPlatformLS.Contracts.Internal.Common.DirectoryPath;

    internal abstract class SyncHandler : IRequestHandler<SyncAgentRequest, SyncAgentResponse, RequestContext>
    {
        private readonly ITokenManager _dataverseTokenManager;
        private readonly CopilotStudio.Sync.IOperationContextProvider _operationContextProvider;
        protected readonly ILspLogger _logger;
        private readonly CopilotStudio.Sync.IIslandControlPlaneService _islandControlPlaneService;
        protected readonly CopilotStudio.Sync.IWorkspaceSynchronizer _synchronizer;
        private readonly ISyncDataverseClient _dataverseClient;
        private readonly LspDataverseHttpClientAccessor _dataverseHttpClientAccessor;

        protected SyncHandler(
            CopilotStudio.Sync.IIslandControlPlaneService islandControlPlaneService,
            CopilotStudio.Sync.IWorkspaceSynchronizer agentWriter,
            ITokenManager tokenManager,
            ISyncDataverseClient dataverseClient,
            LspDataverseHttpClientAccessor dataverseHttpClientAccessor,
            CopilotStudio.Sync.IOperationContextProvider operationContextProvider,
            ILspLogger logger)
        {
            _islandControlPlaneService = islandControlPlaneService;
            _synchronizer = agentWriter ?? throw new ArgumentNullException(nameof(agentWriter));
            _dataverseTokenManager = tokenManager ?? throw new ArgumentNullException(nameof(tokenManager));
            _operationContextProvider = operationContextProvider ?? throw new ArgumentNullException(nameof(operationContextProvider));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _dataverseClient = dataverseClient ?? throw new ArgumentNullException(nameof(dataverseClient));
            _dataverseHttpClientAccessor = dataverseHttpClientAccessor ?? throw new ArgumentNullException(nameof(dataverseHttpClientAccessor));
        }

        public bool MutatesSolutionState => true;

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

                var syncInfo = await _synchronizer.GetSyncInfoAsync(workspace.FolderPath.ToSync());

                var operationContext = await _operationContextProvider.GetAsync(syncInfo);

                var (updatedDefinition, workflowResponse) = await ExecuteAsync(workspace, operationContext, _dataverseClient, syncInfo.AgentId, cancellationToken);
                var (_, localChanges) = await _synchronizer.GetLocalChangesAsync(workspace.FolderPath.ToSync(), updatedDefinition, _dataverseClient, syncInfo.AgentId, cancellationToken);

                return new SyncAgentResponse
                {
                    Code = 200,
                    Message = string.Empty,
                    LocalChanges = localChanges,
                    WorkflowResponse = workflowResponse,
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

        protected abstract Task<(DefinitionBase, ImmutableArray<WorkflowResponse>)> ExecuteAsync(
            IMcsWorkspace workspace,
            AuthoringOperationContextBase operationContext,
            ISyncDataverseClient dataverseClient,
            Guid? agentId,
            CancellationToken cancellationToken);
    }
}
