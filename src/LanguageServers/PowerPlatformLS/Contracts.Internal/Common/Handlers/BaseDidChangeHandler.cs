
namespace Microsoft.PowerPlatformLS.Contracts.Internal.Common.Handlers
{
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models.Lsp;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using System.Threading;
    using System.Threading.Tasks;

    public class BaseDidChangeMethodHandler<DocType> : INotificationHandler<OnDidChangeParams, RequestContext>
        where DocType : LspDocument
    {
        private readonly IDiagnosticsPublisher? _diagnosticsPublisher;
        private readonly ILspLogger? _logger;

        public bool MutatesSolutionState => true;

        /// <summary>
        /// Initializes a new instance of the <see cref="BaseDidChangeMethodHandler{DocType}"/> class.
        /// </summary>
        /// <param name="diagnosticsPublisher">Null if the handler should not emit diagnostics. i.e. file wasn't opened yet</param>
        /// <param name="logger">Used to report changes arriving for documents the server no longer tracks.</param>
        public BaseDidChangeMethodHandler(IDiagnosticsPublisher? diagnosticsPublisher, ILspLogger? logger = null)
        {
            _diagnosticsPublisher = diagnosticsPublisher;
            _logger = logger;
        }

        public async Task HandleNotificationAsync(OnDidChangeParams request, RequestContext context, CancellationToken cancellationToken)
        {
            // The client can still hold an editor open for a document the server has stopped
            // tracking - a watched-file delete event raised while a sync rewrites files untracks it,
            // and the next keystroke lands here. Dereferencing context.Document would throw an
            // unhandled InvalidDataException and tear down the request, so drop the change instead
            // and let the following didOpen / watched-file event resync the document from disk.
            if (context.IsInvalid)
            {
                _logger?.LogWarning($"textDocument/didChange received for a document that is not tracked in the workspace. Ignoring the change; the document resyncs on the next open or file event.");
                return;
            }

            var changes = request.ContentChanges ?? [];

            bool isSyntaxChange = context.Workspace.UpdateDocument(context, changes);

            if (_diagnosticsPublisher != null && isSyntaxChange)
            {
                await _diagnosticsPublisher.PublishDiagnosticsForCurrentDocumentAsync<DocType>(context, cancellationToken, logDiagnostics: false);
            }
        }
    }
}
