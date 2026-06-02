namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.Agents.Platform.Content;
    using Microsoft.Agents.Platform.Content.Exceptions;
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.CopilotStudio.Sync;
    using Microsoft.CopilotStudio.Sync.Dataverse;
    using Microsoft.PowerPlatformLS.Contracts.FileLayout;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Impl.PullAgent.Auth;
    using System.Collections.Immutable;
    using System.Diagnostics;
    using System.Threading;
    using System.Threading.Tasks;

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

        protected abstract string OperationName { get; }

        public async Task<SyncAgentResponse> HandleRequestAsync(SyncAgentRequest request, RequestContext context, CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
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

                var (updatedDefinition, workflowResponse, aiPromptResponse, newlyCreatedCustomConnectors) = await ExecuteAsync(workspace, operationContext, _dataverseClient, syncInfo, cancellationToken);
                var (_, localChanges) = await _synchronizer.GetLocalChangesAsync(workspace.FolderPath, updatedDefinition, _dataverseClient, syncInfo, cancellationToken);

                _logger.LogWarning(
                    "FeatureEvent: feature=sync, operation={0}, outcome=success, durationMs={1}, localChanges={2}",
                    OperationName,
                    stopwatch.ElapsedMilliseconds,
                    localChanges.Length);

                return new SyncAgentResponse
                {
                    Code = 200,
                    Message = string.Empty,
                    LocalChanges = localChanges,
                    WorkflowResponse = workflowResponse,
                    AIPromptResponse = aiPromptResponse,
                    NewlyCreatedCustomConnectors = newlyCreatedCustomConnectors,
                };
            }
            catch (InvalidOperationException ex)
            {
                // User errors
                _logger.LogWarning(
                    "FeatureEvent: feature=sync, operation={0}, outcome=failure, durationMs={1}, errorType={2}, statusCode=400",
                    OperationName,
                    stopwatch.ElapsedMilliseconds,
                    ex.GetType().Name);
                return new SyncAgentResponse
                {
                    Code = 400,
                    Message = ex.Message,
                };
            }
            catch (DataverseServiceUnavailableException ex)
            {
                _logger.LogException(ex);
                _logger.LogWarning(
                    "FeatureEvent: feature=sync, operation={0}, outcome=failure, durationMs={1}, errorType={2}, statusCode=503",
                    OperationName,
                    stopwatch.ElapsedMilliseconds,
                    ex.GetType().Name);
                return new SyncAgentResponse
                {
                    Code = 503,
                    Message = "The Copilot Studio service is temporarily unavailable. Please try again in a moment.",
                };
            }
            catch (Exception ex)
            {
                _logger.LogException(ex);
                _logger.LogWarning(
                    "FeatureEvent: feature=sync, operation={0}, outcome=failure, durationMs={1}, errorType={2}, statusCode=500",
                    OperationName,
                    stopwatch.ElapsedMilliseconds,
                    ex.GetType().Name);
                return new SyncAgentResponse
                {
                    Code = 500,
                    Message = ex.Message,
                };
            }
        }

        protected abstract Task<(DefinitionBase, ImmutableArray<WorkflowResponse>, ImmutableArray<SyncDataverseClient.AIPromptResponse>, ImmutableArray<string>)> ExecuteAsync(
            IMcsWorkspace workspace,
            AuthoringOperationContextBase operationContext,
            ISyncDataverseClient dataverseClient,
            AgentSyncInfo syncInfo,
            CancellationToken cancellationToken);
    }
}
