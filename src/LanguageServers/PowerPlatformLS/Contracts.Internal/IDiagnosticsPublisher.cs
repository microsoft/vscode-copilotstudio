namespace Microsoft.PowerPlatformLS.Contracts.Internal
{
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models.Lsp;
    using System;
    using System.Threading.Tasks;

    public interface IDiagnosticsPublisher
    {
        /// <summary>
        /// Emit a publishDiagnostics notification with no diagnostics for document specified.
        /// Useful when a document with diagnostics is deleted.
        /// </summary>
        Task ClearDiagnosticsAsync(Uri documentUri, CancellationToken cancellationToken);
        Task PublishAllDiagnosticsAsync(RequestContext context, CancellationToken cancellationToken);
        Task PublishDiagnosticsForCurrentDocumentAsync<DocType>(RequestContext context, CancellationToken cancellationToken) where DocType : LspDocument;
    }
}