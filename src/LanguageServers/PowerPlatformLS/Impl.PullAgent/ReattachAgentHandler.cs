namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.Agents.Platform.Content.Exceptions;
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.PowerPlatformLS.Contracts.FileLayout;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Impl.PullAgent.Auth;
    using Microsoft.PowerPlatformLS.Impl.PullAgent.Dataverse;
    using System;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;

    [LanguageServerEndpoint(ReattachAgentRequest.MessageName, LanguageServerConstants.DefaultLanguageName)]
    internal class ReattachAgentHandler : IRequestHandler<ReattachAgentRequest, ReattachAgentResponse, RequestContext>
    {
        private readonly IIslandControlPlaneService _islandControlPlaneService;
        private readonly IWorkspaceSynchronizer _workspaceSynchronizer;
        private readonly ITokenManager _dataverseTokenManager;
        private readonly ILspLogger _logger;
        private readonly IOperationContextProvider _operationContextProvider;
        private readonly Func<string, string, DataverseClient> _dataverseClientFactory;

        public bool MutatesSolutionState => true;

        public ReattachAgentHandler(
            IIslandControlPlaneService islandControlPlaneService,
            IWorkspaceSynchronizer workspaceSynchronizer,
            ITokenManager dataverseTokenManager,
            Func<string, string, DataverseClient> dataverseClientFactory,
            IOperationContextProvider operationContextProvider,
            ILspLogger logger)
        {
            _islandControlPlaneService = islandControlPlaneService;
            _workspaceSynchronizer = workspaceSynchronizer ?? throw new ArgumentNullException(nameof(workspaceSynchronizer));
            _dataverseTokenManager = dataverseTokenManager ?? throw new ArgumentNullException(nameof(dataverseTokenManager));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _operationContextProvider = operationContextProvider ?? throw new ArgumentNullException(nameof(operationContextProvider));
            _dataverseClientFactory = dataverseClientFactory ?? throw new ArgumentNullException(nameof(dataverseClientFactory));
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
            bool isNewAgent = false;

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
                _islandControlPlaneService.SetIslandBaseEndpoint(request.EnvironmentInfo.AgentManagementUrl);
                _dataverseTokenManager.SetTokens(request.DataverseAccessToken, request.CopilotStudioAccessToken);
                var workspace = (IMcsWorkspace)context.Workspace;
                var language = context.Language;

                if (!language.IsValidAgentDirectory(workspaceFolder, out var validDirectory))
                {
                    return new ReattachAgentResponse()
                    {
                        Code = 400,
                        Message = "Agent directory is not valid for reattach.",
                        AgentSyncInfo = defaultSyncInfo
                    };
                }

                if (_workspaceSynchronizer.IsSyncInfoAvailable(workspaceFolder))
                {
                    return new ReattachAgentResponse()
                    {
                        Code = 400,
                        Message = $"This agent is already connected to a cloud instance {request.EnvironmentInfo.AgentManagementUrl}.",
                        AgentSyncInfo = defaultSyncInfo
                    };
                }

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
                        agentDisplayName = bot.Entity.DisplayName;
                    }
                }

                if (!string.IsNullOrWhiteSpace(thisSchema) && !SchemaNameValidator.IsValid(thisSchema))
                {
                    return new ReattachAgentResponse()
                    {
                        Code = 400,
                        Message = $"Invalid schema name '{thisSchema}'.",
                        AgentSyncInfo = defaultSyncInfo
                    };
                }

                var dataverseClient = CreateDataverseClient(request.EnvironmentInfo.DataverseUrl, request.DataverseAccessToken);
                var agentId = await dataverseClient.GetAgentIdBySchemaNameAsync(thisSchema, cancellationToken);
                bool updateWorkspaceDirectory = false;
                if (agentId == Guid.Empty)
                {
                    var newAgent = await dataverseClient.CreateNewAgentAsync(agentDisplayName, thisSchema, cancellationToken);
                    agentId = newAgent.AgentId;
                    isNewAgent = true;

                    if (!string.IsNullOrWhiteSpace(thisSchema) && thisSchema != newAgent.SchemaName)
                    {
                        _logger.LogError($"ReattachAgentInfo: Local schema name '{thisSchema}' is different with the new agent's schema name '{newAgent.SchemaName}'.");
                    }

                    // Update if schema name is new and generated when creating new agent.
                    updateWorkspaceDirectory = thisSchema != newAgent.SchemaName;
                }

                var syncInfo = new AgentSyncInfo()
                {
                    AgentId = agentId,
                    DataverseEndpoint = new Uri(request.EnvironmentInfo.DataverseUrl),
                    EnvironmentId = request.EnvironmentInfo.EnvironmentId,
                    AccountInfo = request.AccountInfo,
                    SolutionVersions = request.SolutionVersions,
                    AgentManagementEndpoint = new Uri(request.EnvironmentInfo.AgentManagementUrl)
                };

                await _workspaceSynchronizer.SaveSyncInfoAsync(workspaceFolder, syncInfo);
                var operationContext = await _operationContextProvider.GetAsync(syncInfo);

                await _workspaceSynchronizer.ProvisionConnectionReferencesAsync(workspace.Definition, dataverseClient, cancellationToken);
                var (workflowResponse, cloudFlowMetadata) = await _workspaceSynchronizer.UpsertWorkflowForAgentAsync(workspaceFolder, dataverseClient, agentId, cancellationToken);
                await _workspaceSynchronizer.SyncWorkspaceAsync(workspaceFolder, operationContext, changeToken: null, updateWorkspaceDirectory, dataverseClient, agentId, cloudFlowMetadata, cancellationToken: cancellationToken);

                return new ReattachAgentResponse()
                {
                    Code = 200,
                    Message = string.Empty,
                    AgentSyncInfo = syncInfo,
                    IsNewAgent = isNewAgent,
                    WorkflowResponse = workflowResponse
                };
            }
            catch (DataverseBadRequestException ex)
            {
                _logger.LogException(ex);
                return new ReattachAgentResponse()
                {
                    Code = ex.StatusCode,
                    Message = ex.Message,
                    AgentSyncInfo = defaultSyncInfo
                };
            }
            catch (Exception ex)
            {
                return new ReattachAgentResponse()
                {
                    Code = 500,
                    Message = ex.Message,
                    AgentSyncInfo = defaultSyncInfo
                };
            }
        }

        protected virtual DataverseClient CreateDataverseClient(string dataverseUrl, string accessToken)
        {
            return _dataverseClientFactory(dataverseUrl, accessToken);
        }
    }
}
