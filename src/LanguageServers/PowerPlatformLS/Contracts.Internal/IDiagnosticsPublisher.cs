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
        /// <summary>Publishes diagnostics for all documents in the workspace.</summary>
        Task PublishAllDiagnosticsAsync(RequestContext context, CancellationToken cancellationToken, bool logDiagnostics = true);

        /// <summary>Publishes diagnostics for the current document.</summary>
        Task PublishDiagnosticsForCurrentDocumentAsync<DocType>(RequestContext context, CancellationToken cancellationToken, bool logDiagnostics = true) where DocType : LspDocument;
    }
}