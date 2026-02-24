namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.PowerPlatformLS.Contracts.FileLayout;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Impl.PullAgent.Auth;
    using System.ComponentModel;
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
        private readonly IOperationContextProvider _operationContextProvider;
        private readonly ILspLogger _logger;
        private readonly IIslandControlPlaneService _islandControlPlaneService;
        protected readonly IWorkspaceSynchronizer _synchronizer;

        public GetRemoteFileHandler(
            IIslandControlPlaneService islandControlPlaneService,
            IWorkspaceSynchronizer agentWriter,
            ITokenManager dataverseTokenManager,
            IOperationContextProvider operationContextProvider,
            ILspLogger logger)
        {
            _islandControlPlaneService = islandControlPlaneService;
            _synchronizer = agentWriter ?? throw new ArgumentNullException(nameof(agentWriter));
            _dataverseTokenManager = dataverseTokenManager ?? throw new ArgumentNullException(nameof(dataverseTokenManager));
            _operationContextProvider = operationContextProvider ?? throw new ArgumentNullException(nameof(operationContextProvider));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public bool MutatesSolutionState => false;

        public async Task<GetFileResponse> HandleRequestAsync(GetFileRequest request, RequestContext context, CancellationToken cancellationToken)
        {
            try
            {
                _islandControlPlaneService.SetIslandBaseEndpoint(request.EnvironmentInfo.AgentManagementUrl);
                _dataverseTokenManager.SetTokens(request.DataverseAccessToken, request.CopilotStudioAccessToken);
                var workspace = (IMcsWorkspace)context.Workspace;
                var syncInfo = await _synchronizer.GetSyncInfoAsync(workspace.FolderPath);
                var operationContext = await _operationContextProvider.GetAsync(syncInfo);
                var (changeSet, changes) = await _synchronizer.GetRemoteChangesAsync(workspace.FolderPath, operationContext, cancellationToken);
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