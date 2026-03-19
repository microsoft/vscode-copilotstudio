namespace Microsoft.PowerPlatformLS.Contracts.Internal.Common.Handlers
{
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common.Framework;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models.Lsp;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using System.Threading;
    using System.Threading.Tasks;

    public class BaseDidOpenHandler<DocType> : INotificationHandler<OnDidOpenParams, RequestContext>
        where DocType : LspDocument
    {
        private readonly IDiagnosticsPublisher _diagnosticsPublisher;

        public BaseDidOpenHandler(IDiagnosticsPublisher diagnosticsPublisher)
        {
            _diagnosticsPublisher = diagnosticsPublisher;
        }

        // upon resolving the didopenparams, we make sure that the document text matches what we have internally
        // if not, we will update the internal representation
        public bool MutatesSolutionState => true;

        public async Task HandleNotificationAsync(OnDidOpenParams request, RequestContext context, CancellationToken cancellationToken)
        {
            await _diagnosticsPublisher.PublishDiagnosticsForCurrentDocumentAsync<DocType>(context, cancellationToken);
        }
    }
}
