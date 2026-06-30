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
        Task PublishAllDiagnosticsAsync(RequestContext context, CancellationToken cancellationToken, bool logDiagnostics = true);

        /// <param name="logDiagnostics">When true, individual diagnostics are logged to the output channel at Debug level.
        /// Pass false for high-frequency triggers (e.g., didChange on every keystroke) to reduce noise.</param>
        Task PublishDiagnosticsForCurrentDocumentAsync<DocType>(RequestContext context, CancellationToken cancellationToken, bool logDiagnostics = true) where DocType : LspDocument;
    }
}