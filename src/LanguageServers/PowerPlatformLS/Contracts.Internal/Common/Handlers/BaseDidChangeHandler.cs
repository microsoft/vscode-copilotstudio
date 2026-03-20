
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

        public bool MutatesSolutionState => true;

        /// <summary>
        /// Initializes a new instance of the <see cref="BaseDidChangeMethodHandler{DocType}"/> class.
        /// </summary>
        /// <param name="diagnosticsPublisher">Null if the handler should not emit diagnostics. i.e. file wasn't opened yet</param>
        public BaseDidChangeMethodHandler(IDiagnosticsPublisher? diagnosticsPublisher)
        {
            _diagnosticsPublisher = diagnosticsPublisher;
        }

        public async Task HandleNotificationAsync(OnDidChangeParams request, RequestContext context, CancellationToken cancellationToken)
        {
            var changes = request.ContentChanges ?? [];

            bool isSyntaxChange = context.Workspace.UpdateDocument(context, changes);

            if (_diagnosticsPublisher != null && isSyntaxChange)
            {
                await _diagnosticsPublisher.PublishDiagnosticsForCurrentDocumentAsync<DocType>(context, cancellationToken);
            }
        }
    }
}
