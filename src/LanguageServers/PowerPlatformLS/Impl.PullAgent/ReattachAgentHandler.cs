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
    /// Binds the connections the client created after <see cref="PrepareReattachRequest"/>, then upserts workflows and AI prompts and syncs the workspace.
    /// Binding runs before the workflow upsert so workflows that depend on those connections succeed.
    /// </summary>
    [LanguageServerEndpoint(ReattachAgentRequest.MessageName, LanguageServerConstants.DefaultLanguageName)]
    internal class ReattachAgentHandler : IRequestHandler<ReattachAgentRequest, ReattachAgentResponse, RequestContext>
    {
        private readonly CopilotStudio.Sync.IIslandControlPlaneService _islandControlPlaneService;
        private readonly CopilotStudio.Sync.IWorkspaceSynchronizer _workspaceSynchronizer;
        private readonly ITokenManager _dataverseTokenManager;
        private readonly ILspLogger _logger;
        private readonly CopilotStudio.Sync.IOperationContextProvider _operationContextProvider;
        private readonly ISyncDataverseClient _dataverseClient;
        private readonly LspDataverseHttpClientAccessor _dataverseHttpClientAccessor;

        public bool MutatesSolutionState => true;

        public ReattachAgentHandler(
            CopilotStudio.Sync.IIslandControlPlaneService islandControlPlaneService,
            CopilotStudio.Sync.IWorkspaceSynchronizer workspaceSynchronizer,
            ITokenManager dataverseTokenManager,
            ISyncDataverseClient dataverseClient,
            LspDataverseHttpClientAccessor dataverseHttpClientAccessor,
            CopilotStudio.Sync.IOperationContextProvider operationContextProvider,
            ILspLogger logger)
        {
            _islandControlPlaneService = islandControlPlaneService;
            _workspaceSynchronizer = workspaceSynchronizer ?? throw new ArgumentNullException(nameof(workspaceSynchronizer));
            _dataverseTokenManager = dataverseTokenManager ?? throw new ArgumentNullException(nameof(dataverseTokenManager));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _operationContextProvider = operationContextProvider ?? throw new ArgumentNullException(nameof(operationContextProvider));
            _dataverseClient = dataverseClient ?? throw new ArgumentNullException(nameof(dataverseClient));
            _dataverseHttpClientAccessor = dataverseHttpClientAccessor ?? throw new ArgumentNullException(nameof(dataverseHttpClientAccessor));
        }

        /// <summary>
        /// Handle reattach agent request.
        /// </summary>
        /// <param name="request">Reattach request</param>
        /// <param name="context">Context request</param>
        /// <param name="cancellationToken">Cancelation token</param>
        /// <returns>Reattached agent response</returns>
        public async Task<ReattachAgentResponse> HandleRequestAsync(ReattachAgentRequest request, RequestContext context, CancellationToken cancellationToken)
        {
            var workspaceFolder = request.WorkspaceUri.ToDirectoryPath();

            var defaultSyncInfo = new AgentSyncInfo()
            {
                AgentId = Guid.Empty,
                DataverseEndpoint = new Uri(request.EnvironmentInfo.DataverseUrl),
                EnvironmentId = request.EnvironmentInfo.EnvironmentId,
                AccountInfo = request.AccountInfo,
                SolutionVersions = request.SolutionVersions,
                AgentManagementEndpoint = new Uri(request.EnvironmentInfo.AgentManagementUrl)
            };

            try
            {
                ConnectionHelper.ApplyConnectionContext(_islandControlPlaneService, _dataverseTokenManager, _dataverseHttpClientAccessor, _dataverseClient, request);

                var workspace = (IMcsWorkspace)context.Workspace;
                var language = context.Language;

                if (!language.IsValidAgentDirectory(workspaceFolder, out _))
                {
                    return new ReattachAgentResponse()
                    {
                        Code = 400,
                        Message = "Agent directory is not valid for reattach. Try opening root of the selected agent folder.",
                        AgentSyncInfo = defaultSyncInfo
                    };
                }

                var classification = AgentClassifier.Classify(workspace.Definition, workspaceFolder.ToString());
                if (!classification.Allows(SyncOperation.Reattach))
                {
                    return new ReattachAgentResponse()
                    {
                        Code = 400,
                        Message = AuthoringSupportGate.DescribeBlocked(classification, SyncOperation.Reattach),
                        AgentSyncInfo = defaultSyncInfo
                    };
                }

                if (_workspaceSynchronizer.IsSyncInfoAvailable(workspaceFolder))
                {
                    return new ReattachAgentResponse()
                    {
                        Code = 400,
                        Message = "This agent is already connected to a cloud instance.",
                        AgentSyncInfo = defaultSyncInfo
                    };
                }

                if (request.AgentSyncInfo is null)
                {
                    return new ReattachAgentResponse()
                    {
                        Code = 400,
                        Message = "Reattach was not prepared for this agent. Run reattach again.",
                        AgentSyncInfo = defaultSyncInfo
                    };
                }

                var syncInfo = request.AgentSyncInfo;
                var agentId = syncInfo.AgentId;
                var operationContext = await _operationContextProvider.GetAsync(syncInfo);

                await ConnectionHelper.BindConnectionsAsync(_dataverseClient, request.ConnectionBindings, _logger, cancellationToken);

                var (workflowResponse, cloudFlowMetadata) = await _workspaceSynchronizer.UpsertWorkflowForAgentAsync(workspaceFolder, _dataverseClient, agentId, cancellationToken);
                var (aiPromptResponse, _) = await _workspaceSynchronizer.UpsertAIPromptsForAgentAsync(workspaceFolder, _dataverseClient, agentId, cancellationToken);
                await _workspaceSynchronizer.SyncWorkspaceAsync(workspaceFolder, operationContext, changeToken: null, request.UpdateWorkspaceDirectory, _dataverseClient, syncInfo, cloudFlowMetadata, cancellationToken: cancellationToken);
                await _workspaceSynchronizer.SaveSyncInfoAsync(workspaceFolder, syncInfo);

                return new ReattachAgentResponse()
                {
                    Code = 200,
                    Message = string.Empty,
                    AgentSyncInfo = syncInfo,
                    IsNewAgent = request.IsNewAgent,
                    WorkflowResponse = workflowResponse,
                    AIPromptResponse = aiPromptResponse,
                };
            }
            catch (Exception ex)
            {
                var (code, message) = LspExceptionHandler.Handle(ex, _logger, cancellationToken);
                return new ReattachAgentResponse()
                {
                    Code = code,
                    Message = message,
                    AgentSyncInfo = defaultSyncInfo
                };
            }
        }
    }
}
