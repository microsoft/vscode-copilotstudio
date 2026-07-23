namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.CopilotStudio.Sync;
    using Microsoft.PowerPlatformLS.Contracts.FileLayout;
    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using System.Threading;
    using System.Threading.Tasks;

    internal class DiscardLocalChangesRequest : IHasWorkspace
    {
        public required Uri WorkspaceUri { get; set; }
    }

    internal class DiscardLocalChangesResponse : SyncAgentResponse
    {
        public DiscardResult Result { get; set; } = new();
    }

    [LanguageServerEndpoint(Constants.JsonRpcMethods.DiscardLocalChanges, LanguageServerConstants.DefaultLanguageName)]
    internal class DiscardLocalChangesHandler : IRequestHandler<DiscardLocalChangesRequest, DiscardLocalChangesResponse, RequestContext>
    {
        private readonly IWorkspaceSynchronizer _workspaceSynchronizer;
        private readonly ILspLogger _logger;

        public DiscardLocalChangesHandler(IWorkspaceSynchronizer workspaceSynchronizer, ILspLogger logger)
        {
            _workspaceSynchronizer = workspaceSynchronizer ?? throw new ArgumentNullException(nameof(workspaceSynchronizer));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public bool MutatesSolutionState => true;

        public async Task<DiscardLocalChangesResponse> HandleRequestAsync(
            DiscardLocalChangesRequest request,
            RequestContext context,
            CancellationToken cancellationToken)
        {
            try
            {
                var workspace = (IMcsWorkspace)context.Workspace;
                var definition = await _workspaceSynchronizer
                    .ReadWorkspaceDefinitionAsync(workspace.FolderPath, cancellationToken, checkKnowledgeFiles: true)
                    .ConfigureAwait(false);
                var (_, localChanges) = await _workspaceSynchronizer
                    .GetLocalChangesAsync(workspace.FolderPath, definition, cancellationToken)
                    .ConfigureAwait(false);
                var result = _workspaceSynchronizer.DiscardLocalChanges(workspace.FolderPath, localChanges);

                var updatedDefinition = await _workspaceSynchronizer
                    .ReadWorkspaceDefinitionAsync(workspace.FolderPath, cancellationToken, checkKnowledgeFiles: true)
                    .ConfigureAwait(false);
                var (_, remainingChanges) = await _workspaceSynchronizer
                    .GetLocalChangesAsync(workspace.FolderPath, updatedDefinition, cancellationToken)
                    .ConfigureAwait(false);

                return new DiscardLocalChangesResponse
                {
                    Code = 200,
                    Message = string.Empty,
                    Result = result,
                    LocalChanges = remainingChanges,
                };
            }
            catch (Exception exception)
            {
                var (code, message) = LspExceptionHandler.Handle(exception, _logger, cancellationToken);
                return new DiscardLocalChangesResponse
                {
                    Code = code,
                    Message = message,
                };
            }
        }
    }
}
