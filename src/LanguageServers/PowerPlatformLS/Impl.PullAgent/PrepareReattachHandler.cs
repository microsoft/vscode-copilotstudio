namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.Agents.ObjectModel;
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
    /// Prepares connections for a reattach: validates the agent directory, provisions the cloud agent (creating it when missing), provisions custom connectors and connection references, then returns the connection
    /// references the client must fulfill before finalizing the reattach (<see cref="ReattachAgentRequest"/>).
    /// </summary>
    [LanguageServerEndpoint(PrepareReattachRequest.MessageName, LanguageServerConstants.DefaultLanguageName)]
    internal class PrepareReattachHandler : IRequestHandler<PrepareReattachRequest, PrepareReattachResponse, RequestContext>
    {
        private readonly IIslandControlPlaneService _islandControlPlaneService;
        private readonly IWorkspaceSynchronizer _workspaceSynchronizer;
        private readonly ITokenManager _dataverseTokenManager;
        private readonly ISyncDataverseClient _dataverseClient;
        private readonly LspDataverseHttpClientAccessor _dataverseHttpClientAccessor;
        private readonly ILspLogger _logger;

        public bool MutatesSolutionState => true;

        public PrepareReattachHandler(
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

        public async Task<PrepareReattachResponse> HandleRequestAsync(PrepareReattachRequest request, RequestContext context, CancellationToken cancellationToken)
        {
            var defaultSyncInfo = ConnectionHelper.BuildDefaultSyncInfo(request);

            try
            {
                ConnectionHelper.ApplyConnectionContext(_islandControlPlaneService, _dataverseTokenManager, _dataverseHttpClientAccessor, _dataverseClient, request);

                var workspace = (IMcsWorkspace)context.Workspace;
                var workspaceFolder = request.WorkspaceUri.ToDirectoryPath();
                bool isNewAgent = false;
                var language = context.Language;

                if (!language.IsValidAgentDirectory(workspaceFolder, out _))
                {
                    return CreateErrorResponse(400, "Agent directory is not valid for reattach. Try opening root of the selected agent folder.", defaultSyncInfo);
                }

                if (_workspaceSynchronizer.IsSyncInfoAvailable(workspaceFolder))
                {
                    return CreateErrorResponse(400, "This agent is already connected to a cloud instance.", defaultSyncInfo);
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
                    return CreateErrorResponse(400, $"Invalid schema name '{thisSchema}'.", defaultSyncInfo);
                }

                var classification = AgentClassifier.Classify(workspace.Definition, workspaceFolder.ToString());
                if (!classification.Allows(SyncOperation.Reattach))
                {
                    return CreateErrorResponse(400, AuthoringSupportGate.DescribeBlocked(classification, SyncOperation.Reattach), defaultSyncInfo);
                }

                var agentId = await _dataverseClient.GetAgentIdBySchemaNameAsync(thisSchema, cancellationToken);
                bool updateWorkspaceDirectory = false;
                if (agentId == Guid.Empty)
                {
                    var authoringShape = workspace.AuthoringShape;
                    if (authoringShape == AuthoringShape.Unknown)
                    {
                        authoringShape = AgentClassifier.DetectAuthoringShapeFromFolder(workspaceFolder.ToString());
                    }

                    var newAgent = await _dataverseClient.CreateNewAgentAsync(agentDisplayName, thisSchema, authoringShape, cancellationToken);
                    agentId = newAgent.AgentId;
                    isNewAgent = true;

                    if (!string.IsNullOrWhiteSpace(thisSchema) && thisSchema != newAgent.SchemaName)
                    {
                        _logger.LogSensitiveInformation($"PrepareReattachInfo: Local schema name '{thisSchema}' is different with the new agent's schema name '{newAgent.SchemaName}'.", "PrepareReattachInfo: Local schema name differs from the new agent's schema name.");
                    }

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

                var agentConnections = await ConnectionHelper.ProvisionAndGetConnectionsAsync(
                    _workspaceSynchronizer, workspaceFolder, workspace.Definition, _dataverseClient, cancellationToken);

                return new PrepareReattachResponse()
                {
                    Code = 200,
                    Message = string.Empty,
                    AgentSyncInfo = syncInfo,
                    IsNewAgent = isNewAgent,
                    UpdateWorkspaceDirectory = updateWorkspaceDirectory,
                    AgentConnections = agentConnections,
                };
            }
            catch (DataverseBadRequestException ex)
            {
                _logger.LogException(ex);
                return CreateErrorResponse(ex.StatusCode, ex.Message, defaultSyncInfo);
            }
            catch (Exception ex)
            {
                _logger.LogException(ex);
                return CreateErrorResponse(500, ex.Message, defaultSyncInfo);
            }
        }

        private static PrepareReattachResponse CreateErrorResponse(int code, string message, AgentSyncInfo defaultSyncInfo) => new()
        {
            Code = code,
            Message = message,
            AgentSyncInfo = defaultSyncInfo,
        };
    }
}
