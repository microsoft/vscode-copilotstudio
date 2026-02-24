namespace Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Handlers
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Models;
    using System.Threading;
    using System.Threading.Tasks;

    internal class GetFileRequest : IHasWorkspace
    {
        public required Uri WorkspaceUri { get; set; }

        public required string SchemaName { get; set; }
    }

    internal class GetFileResponse : ResponseBase
    {
        public string? Content { get; set; }
    }

    [LanguageServerEndpoint(EndpointName, LanguageServerConstants.DefaultLanguageName)]
    internal class GetCloudCacheFileHandler : IRequestHandler<GetFileRequest, GetFileResponse, RequestContext>
    {
        private const string EndpointName = "powerplatformls/getCachedFile";
        private readonly ILspLogger _logger;

        public GetCloudCacheFileHandler(ILspLogger logger)
        {
            _logger = logger;
        }

        public bool MutatesSolutionState => false;

        public Task<GetFileResponse> HandleRequestAsync(GetFileRequest request, RequestContext context, CancellationToken cancellationToken)
        {
            try
            {
                var workspacePath = context.Workspace.FolderPath;
                var path = workspacePath.GetChildFilePath(".mcs/botdefinition.json");
                var originalDefinition = (context.Workspace.GetDocument(path) as McsLspDocument)?.FileModel as DefinitionBase;
                var contextDefinition = (context.Workspace as McsWorkspace)?.Definition;

                using var sw = new StringWriter();
                if (request.SchemaName.Equals("entity", StringComparison.OrdinalIgnoreCase) && originalDefinition is BotDefinition bd && bd.Entity is not null)
                {
                    CodeSerializer.SerializeWithoutKind(sw, bd.Entity.WithOnlySettingsYamlProperties());
                }
                else if (request.SchemaName.Equals("collection", StringComparison.OrdinalIgnoreCase) && originalDefinition is BotComponentCollectionDefinition cc && cc.ComponentCollection is not null)
                {
                    CodeSerializer.SerializeWithoutKind(sw, cc.ComponentCollection.WithOnlyYamlFileProperties());
                }
                else if (originalDefinition != null && originalDefinition.TryGetComponentBySchemaName(request.SchemaName, out var component))
                {
                    CodeSerializer.SerializeAsMcsYml(sw, component);
                }

                // Fallback to context definition if not found in original definition. File has been added but not yet pushed.
                else if (contextDefinition != null && contextDefinition.TryGetComponentBySchemaName(request.SchemaName, out var componentByName))
                {
                    CodeSerializer.SerializeAsMcsYml(sw, componentByName);
                }
                else
                {
                    const int NotFoundErrorCode = 404;
                    var errorMessage = originalDefinition != null ? $"Schema name '{request.SchemaName}' not found in the cached file." : "Cached file not found.";
                    _logger.LogWarning($"{EndpointName} returns {NotFoundErrorCode} with error={errorMessage}");
                    return Task.FromResult(new GetFileResponse
                    {
                        Code = NotFoundErrorCode,
                        Message = errorMessage,
                    });
                }

                return Task.FromResult(new GetFileResponse
                {
                    Code = 200,
                    Content = sw.ToString(),
                });
            }
            catch (Exception ex)
            {
                const int InternalErrorCode = 500;
                _logger.LogError($"{EndpointName} returns {InternalErrorCode} with error={ex.Message}");
                return Task.FromResult(new GetFileResponse
                {
                    Code = InternalErrorCode,
                    Message = ex.Message,
                });
            }
        }
    }
}