namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.Agents.ObjectModel;
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
    /// Reattaches a local agent workspace to a cloud environment.
    /// </summary>
    [LanguageServerEndpoint(ReattachAgentRequest.MessageName, LanguageServerConstants.DefaultLanguageName)]
    internal class ReattachAgentHandler : IRequestHandler<ReattachAgentRequest, ReattachAgentResponse, RequestContext>
    {
        private readonly CopilotStudio.Sync.IIslandControlPlaneService _islandControlPlaneService;
        private readonly CopilotStudio.Sync.IWorkspaceSynchronizer _workspaceSynchronizer;
        private readonly CopilotStudio.Sync.IWorkspaceRetargetService _retargetService;
        private readonly ITokenManager _dataverseTokenManager;
        private readonly ILspLogger _logger;
        private readonly CopilotStudio.Sync.IOperationContextProvider _operationContextProvider;
        private readonly ISyncDataverseClient _dataverseClient;
        private readonly LspDataverseHttpClientAccessor _dataverseHttpClientAccessor;

        public bool MutatesSolutionState => true;

        public ReattachAgentHandler(
            CopilotStudio.Sync.IIslandControlPlaneService islandControlPlaneService,
            CopilotStudio.Sync.IWorkspaceSynchronizer workspaceSynchronizer,
            CopilotStudio.Sync.IWorkspaceRetargetService retargetService,
            ITokenManager dataverseTokenManager,
            ISyncDataverseClient dataverseClient,
            LspDataverseHttpClientAccessor dataverseHttpClientAccessor,
            CopilotStudio.Sync.IOperationContextProvider operationContextProvider,
            ILspLogger logger)
        {
            _islandControlPlaneService = islandControlPlaneService;
            _workspaceSynchronizer = workspaceSynchronizer ?? throw new ArgumentNullException(nameof(workspaceSynchronizer));
            _retargetService = retargetService ?? throw new ArgumentNullException(nameof(retargetService));
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
            var defaultSyncInfo = ConnectionHelper.BuildDefaultSyncInfo(request);

            try
            {
                ConnectionHelper.ApplyConnectionContext(_islandControlPlaneService, _dataverseTokenManager, _dataverseHttpClientAccessor, _dataverseClient, request);

                var workspace = (IMcsWorkspace)context.Workspace;
                var language = context.Language;

                if (!language.IsValidAgentDirectory(workspaceFolder, out _))
                {
                    return CreateErrorResponse(400, "Agent directory is not valid for reattach. Try opening root of the selected agent folder.", defaultSyncInfo);
                }

                var isSyncInfoAvailable = _workspaceSynchronizer.IsSyncInfoAvailable(workspaceFolder);
                if (isSyncInfoAvailable && !request.AllowRetarget)
                {
                    return CreateErrorResponse(400, "This agent is already connected to a cloud instance.", defaultSyncInfo);
                }

                var isRetarget = isSyncInfoAvailable && request.AllowRetarget;

                string thisSchema = string.Empty;
                string agentDisplayName = "ReattachAgent";

                if (workspace.Definition is BotComponentCollectionDefinition collection)
                {
                    thisSchema = collection.GetRootSchemaName();
                }
                else if (workspace.Definition is BotDefinition bot)
                {
                    thisSchema = bot.GetRootSchemaName();
                    if (!string.IsNullOrEmpty(bot.Entity?.DisplayName))
                    {
                        agentDisplayName = bot.Entity!.DisplayName!;
                    }
                }

                if (!string.IsNullOrWhiteSpace(thisSchema) && !SchemaNameValidator.IsValid(thisSchema))
                {
                    return CreateErrorResponse(400, $"Invalid schema name '{thisSchema}'.", defaultSyncInfo);
                }

                var classification = AgentClassifier.Classify(workspace.Definition, workspaceFolder.ToString());
                if (!classification.Allows(SyncOperation.Reattach))
                {
                    return CreateErrorResponse(400, AuthoringSupportGate.DescribeBlocked(classification, SyncOperation.Reattach), defaultSyncInfo);
                }

                var schemaAgentId = await _dataverseClient.GetAgentIdBySchemaNameAsync(thisSchema, cancellationToken);

                if (isRetarget && request.ConflictResolution == RetargetConflictResolution.Prompt && schemaAgentId != Guid.Empty)
                {
                    return new ReattachAgentResponse()
                    {
                        Code = 200,
                        Message = string.Empty,
                        AgentSyncInfo = defaultSyncInfo,
                        SchemaConflict = true,
                    };
                }

                var agentId = schemaAgentId;
                var isNewAgent = false;
                var updateWorkspaceDirectory = false;
                var createNewAgent = agentId == Guid.Empty;

                if (createNewAgent)
                {
                    var authoringShape = workspace.AuthoringShape == AuthoringShape.Unknown ? AgentClassifier.DetectAuthoringShapeFromFolder(workspaceFolder.ToString()) : workspace.AuthoringShape;
                    var newAgent = await _dataverseClient.CreateNewAgentAsync(agentDisplayName, thisSchema, authoringShape, cancellationToken);
                    agentId = newAgent.AgentId;
                    isNewAgent = true;
                    updateWorkspaceDirectory = !string.IsNullOrWhiteSpace(thisSchema) && thisSchema != newAgent.SchemaName;
                    if (updateWorkspaceDirectory)
                    {
                        _logger.LogSensitiveInformation($"ReattachInfo: Local schema name '{thisSchema}' is different with the new agent's schema name '{newAgent.SchemaName}'.", "ReattachInfo: Local schema name differs from the new agent's schema name.");
                    }
                }

                var syncInfo = new AgentSyncInfo()
                {
                    AgentId = agentId,
                    DataverseEndpoint = new Uri(request.EnvironmentInfo.DataverseUrl),
                    EnvironmentId = request.EnvironmentInfo.EnvironmentId,
                    EnvironmentDisplayName = request.EnvironmentInfo.DisplayName,
                    AccountInfo = request.AccountInfo,
                    SolutionVersions = request.SolutionVersions,
                    AgentManagementEndpoint = new Uri(request.EnvironmentInfo.AgentManagementUrl)
                };

                var operationContext = await _operationContextProvider.GetAsync(syncInfo);

                RemoteBindingSnapshot? bindingSnapshot = null;
                if (isRetarget)
                {
                    bindingSnapshot = _retargetService.ResetRemoteBindingState(workspaceFolder);
                }
                else
                {
                    _workspaceSynchronizer.ClearComponentSyncBaselines(workspaceFolder);
                }

                try
                {
                    await ConnectionHelper.ProvisionConnectionsAsync(_workspaceSynchronizer, workspaceFolder, workspace.Definition, _dataverseClient, cancellationToken);

                    var (workflowResponse, cloudFlowMetadata) = await _workspaceSynchronizer.UpsertWorkflowForAgentAsync(workspaceFolder, _dataverseClient, agentId, cancellationToken, CopilotStudio.Sync.WorkflowActivationMode.DraftWhenConnectionReferencesExist);
                    var (aiPromptResponse, aiPromptMetadata) = await _workspaceSynchronizer.UpsertAIPromptsForAgentAsync(workspaceFolder, _dataverseClient, agentId, cancellationToken);

                    await _retargetService.SyncWorkspaceAsync(workspaceFolder, operationContext, null, updateWorkspaceDirectory, _dataverseClient, syncInfo, cloudFlowMetadata, cancellationToken, aiPromptMetadata, syncCustomConnectors: false);
                    await _workspaceSynchronizer.SaveSyncInfoAsync(workspaceFolder, syncInfo);

                    return new ReattachAgentResponse()
                    {
                        Code = 200,
                        Message = string.Empty,
                        AgentSyncInfo = syncInfo,
                        IsNewAgent = isNewAgent,
                        RequiresLocalPush = isNewAgent || isRetarget,
                        WorkflowResponse = workflowResponse,
                        AIPromptResponse = aiPromptResponse,
                    };
                }
                catch when (bindingSnapshot != null)
                {
                    _retargetService.RestoreRemoteBindingState(workspaceFolder, bindingSnapshot!);
                    throw;
                }
            }
            catch (Exception ex)
            {
                var (code, message) = LspExceptionHandler.Handle(ex, _logger, cancellationToken);
                return CreateErrorResponse(code, message, defaultSyncInfo);
            }
        }

        private static ReattachAgentResponse CreateErrorResponse(int code, string message, AgentSyncInfo defaultSyncInfo) => new()
        {
            Code = code,
            Message = message,
            AgentSyncInfo = defaultSyncInfo,
        };
    }
}
