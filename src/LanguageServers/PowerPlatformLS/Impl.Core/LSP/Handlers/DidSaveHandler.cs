namespace Microsoft.PowerPlatformLS.Impl.Core.Lsp.Handlers
{
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Make sure local cache is up-to-date with file content;
    /// It should be up-to-date 99.9% through didChange events but corner case might cause inconsistency (e.g. file edited in another editor and saved in active window).
    ///
    /// After update, triggers workspace-wide validation and emit diagnostics asynchronously.
    /// </summary>
    [LanguageServerEndpoint(LspMethods.DidSave, LanguageServerConstants.DefaultLanguageName)]
    internal class DidSaveHandler : INotificationHandler<DidSaveTextDocumentParams, RequestContext>
    {
        private readonly IDiagnosticsPublisher _diagnosticsPublisher;
        private readonly ILspTransport _transport;

        public bool MutatesSolutionState => true;

        public DidSaveHandler(ILspTransport transport, IDiagnosticsPublisher diagnosticsPublisher)
        {
            _diagnosticsPublisher = diagnosticsPublisher;
            _transport = transport;
        }

        public async Task HandleNotificationAsync(DidSaveTextDocumentParams request, RequestContext requestContext, CancellationToken cancellationToken)
        {
            if (request.Text != null)
            {
                requestContext.Document.UpdateText(request.Text);
            }

            await _diagnosticsPublisher.PublishAllDiagnosticsAsync(requestContext, cancellationToken);
        }
    }
}
