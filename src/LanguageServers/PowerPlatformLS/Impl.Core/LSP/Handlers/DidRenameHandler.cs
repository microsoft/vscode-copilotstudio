namespace Microsoft.PowerPlatformLS.Impl.Core.Lsp.Handlers
{
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using System.Threading;
    using System.Threading.Tasks;

    [LanguageServerEndpoint(LspMethods.DidRename, LanguageServerConstants.DefaultLanguageName)]
    internal class DidRenameHandler : INotificationHandler<RenameFilesParams, RequestContext>
    {
        private readonly IRequestContextResolver _contextResolver;
        private readonly IDiagnosticsPublisher _diagnosticsPublisher;

        public DidRenameHandler(IRequestContextResolver contextResolver, IDiagnosticsPublisher diagnosticsPublisher)
        {
            _contextResolver = contextResolver;
            _diagnosticsPublisher = diagnosticsPublisher;
        }

        public bool MutatesSolutionState => false;

        public async Task HandleNotificationAsync(RenameFilesParams request, RequestContext _, CancellationToken cancellationToken)
        {
            foreach (var file in request.Files)
            {
                // If file renaming with same chars but different casing, dont include/exclude the file from the workspace.
                if (file.ShouldIgnore())
                {
                    continue;
                }

                var oldRequestContext = _contextResolver.Resolve(new TextDocumentIdentifier { Uri = file.OldUri });
                if (!oldRequestContext.IsInvalid)
                {
                    oldRequestContext.Workspace.RemoveDocument(file.OldUri.ToFilePath());
                    oldRequestContext.Workspace.BuildCompilationModel();
                }

                // client is dumb and doesn't know it can clear diagnostics for old file after it's renamed.
                // this is the main reason why we need to subscribe to "didRename" events
                await _diagnosticsPublisher.ClearDiagnosticsAsync(file.OldUri, cancellationToken);

                var newContext = _contextResolver.Resolve(new TextDocumentIdentifier { Uri = file.NewUri });
                await _diagnosticsPublisher.PublishAllDiagnosticsAsync(newContext, cancellationToken);
            }
        }
    }
}
