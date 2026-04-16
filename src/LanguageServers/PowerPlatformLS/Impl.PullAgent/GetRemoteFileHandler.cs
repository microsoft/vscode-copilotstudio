namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.CopilotStudio.Sync;
    using Microsoft.CopilotStudio.Sync.Dataverse;
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.PowerPlatformLS.Contracts.FileLayout;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Impl.PullAgent.Auth;
    using System.Threading;
    using System.Threading.Tasks;


    internal class GetFileRequest : DataverseRequest, IHasWorkspace
    {
        public required Uri WorkspaceUri { get; set; }
        public required string SchemaName { get; set; }
    }

    internal class GetFileResponse : ResponseBase
    {
        public string? Content { get; set; }
    }

    [LanguageServerEndpoint("powerplatformls/getRemoteFile", LanguageServerConstants.DefaultLanguageName)]
    internal class GetRemoteFileHandler : IRequestHandler<GetFileRequest, GetFileResponse, RequestContext>
    {
        private readonly ITokenManager _dataverseTokenManager;
        private readonly CopilotStudio.Sync.IOperationContextProvider _operationContextProvider;
        private readonly ILspLogger _logger;
        private readonly CopilotStudio.Sync.IIslandControlPlaneService _islandControlPlaneService;
        protected readonly CopilotStudio.Sync.IWorkspaceSynchronizer _synchronizer;
        private readonly ISyncDataverseClient _dataverseClient;
        private readonly LspDataverseHttpClientAccessor _dataverseHttpClientAccessor;

        public GetRemoteFileHandler(
            CopilotStudio.Sync.IIslandControlPlaneService islandControlPlaneService,
            CopilotStudio.Sync.IWorkspaceSynchronizer agentWriter,
            ITokenManager dataverseTokenManager,
            CopilotStudio.Sync.IOperationContextProvider operationContextProvider,
            ISyncDataverseClient dataverseClient,
            LspDataverseHttpClientAccessor dataverseHttpClientAccessor,
            ILspLogger logger)
        {
            _islandControlPlaneService = islandControlPlaneService;
            _synchronizer = agentWriter ?? throw new ArgumentNullException(nameof(agentWriter));
            _dataverseTokenManager = dataverseTokenManager ?? throw new ArgumentNullException(nameof(dataverseTokenManager));
            _operationContextProvider = operationContextProvider ?? throw new ArgumentNullException(nameof(operationContextProvider));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _dataverseClient = dataverseClient ?? throw new ArgumentNullException(nameof(dataverseClient));
            _dataverseHttpClientAccessor = dataverseHttpClientAccessor ?? throw new ArgumentNullException(nameof(dataverseHttpClientAccessor));
        }

        public bool MutatesSolutionState => false;

        public async Task<GetFileResponse> HandleRequestAsync(GetFileRequest request, RequestContext context, CancellationToken cancellationToken)
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
                var syncInfo = await _synchronizer.GetSyncInfoAsync(workspace.FolderPath);
                var operationContext = await _operationContextProvider.GetAsync(syncInfo);
                var (changeSet, changes) = await _synchronizer.GetRemoteChangesAsync(workspace.FolderPath, operationContext, _dataverseClient, syncInfo.AgentId, cancellationToken);
                var change = changes.FirstOrDefault(m => m.SchemaName.Equals(request.SchemaName, StringComparison.OrdinalIgnoreCase));

                if (change == null)
                {
                    return new GetFileResponse
                    {
                        Code = 404,
                        Message = string.Empty,
                    };
                }

                if (change.ChangeType == ChangeType.Delete)
                {
                    return new GetFileResponse
                    {
                        Code = 200,
                        Content = string.Empty,
                    };
                }

                if (request.SchemaName.StartsWith("Mcs.Workflow."))
                {
                    var workflowId = request.SchemaName.Substring("Mcs.Workflow.".Length);
                    var workflowChange = changes.FirstOrDefault(c => c.SchemaName.Equals(request.SchemaName, StringComparison.OrdinalIgnoreCase));

                    if (workflowChange != null && !string.IsNullOrWhiteSpace(workflowChange.RemoteWorkflowContent))
                    {
                        return new GetFileResponse
                        {
                            Code = 200,
                            Content = workflowChange.RemoteWorkflowContent
                        };
                    }
                }

                using var sw = new StringWriter();
                if (request.SchemaName.Equals("entity", StringComparison.OrdinalIgnoreCase))
                {
                    if (changeSet.Bot != null)
                    {
                        CodeSerializer.SerializeWithoutKind(sw, changeSet.Bot.WithOnlySettingsYamlProperties());
                    }

                }
                else if (request.SchemaName.Equals("collection", StringComparison.OrdinalIgnoreCase))
                {
                    var component = changeSet.ComponentCollectionChanges.OfType<BotComponentCollectionUpsert>().FirstOrDefault(b => b.ComponentCollection?.SchemaName == request.SchemaName);
                    if (component?.ComponentCollection != null)
                    {
                        CodeSerializer.SerializeWithoutKind(sw, component.ComponentCollection.WithOnlyYamlFileProperties());
                    }
                }
                else
                {
                    var component = changeSet.BotComponentChanges.OfType<BotComponentUpsert>().FirstOrDefault(b => b.Component?.SchemaNameString == request.SchemaName);
                    if (component != null)
                    {
                        CodeSerializer.SerializeAsMcsYml(sw, component.Component);
                    }

                    var environmentVariable = changeSet.EnvironmentVariableChanges.OfType<EnvironmentVariableUpsert>().FirstOrDefault(b => b.EnvironmentVariable?.SchemaName.Value == request.SchemaName);
                    if (environmentVariable?.EnvironmentVariable != null)
                    {
                        CodeSerializer.Serialize(sw, environmentVariable.EnvironmentVariable);
                    }
                }

                return new GetFileResponse
                {
                    Code = 200,
                    Content = sw.ToString(),
                };

            }
            catch (Exception ex)
            {
                return new GetFileResponse
                {
                    Code = 500,
                    Message = ex.Message,
                };
            }
        }
    }
}
