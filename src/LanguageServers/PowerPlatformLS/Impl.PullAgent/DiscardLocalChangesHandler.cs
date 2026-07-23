namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.CopilotStudio.Sync;
    using Microsoft.PowerPlatformLS.Contracts.FileLayout;
    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using System.Collections.Immutable;
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
                var diffDefinition = OverlayCompiledComponentCollections(
                    definition,
                    workspace.Definition);
                var (_, localChanges) = await _workspaceSynchronizer
                    .GetLocalChangesAsync(workspace.FolderPath, diffDefinition, cancellationToken)
                    .ConfigureAwait(false);
                var result = _workspaceSynchronizer.DiscardLocalChanges(
                    workspace.FolderPath,
                    definition,
                    localChanges);

                var updatedDefinition = await _workspaceSynchronizer
                    .ReadWorkspaceDefinitionAsync(workspace.FolderPath, cancellationToken, checkKnowledgeFiles: true)
                    .ConfigureAwait(false);
                var (_, remainingChanges) = await _workspaceSynchronizer
                    .GetLocalChangesAsync(workspace.FolderPath, updatedDefinition, cancellationToken)
                    .ConfigureAwait(false);
                var skippedChanges = localChanges.Where(change =>
                    result.Skipped.Any(skipped =>
                        string.Equals(skipped.SchemaName, change.SchemaName, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(skipped.Path, change.Uri, StringComparison.OrdinalIgnoreCase)));
                remainingChanges = remainingChanges
                    .AddRange(skippedChanges)
                    .DistinctBy(change => (change.SchemaName.ToUpperInvariant(), change.Uri.ToUpperInvariant()))
                    .ToImmutableArray();

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

        private static DefinitionBase OverlayCompiledComponentCollections(
            DefinitionBase projectedDefinition,
            DefinitionBase compiledDefinition)
        {
            return projectedDefinition is BotDefinition projectedBot
                && compiledDefinition is BotDefinition compiledBot
                ? projectedBot.WithComponentCollections(compiledBot.ComponentCollections)
                : projectedDefinition;
        }
    }
}
