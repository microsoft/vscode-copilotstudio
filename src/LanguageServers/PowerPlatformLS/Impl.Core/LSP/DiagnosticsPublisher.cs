namespace Microsoft.PowerPlatformLS.Impl.Core.Lsp
{
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common.Framework;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;

    internal class DiagnosticsPublisher : IDiagnosticsPublisher
    {
        private readonly ILspServices _lspServices;
        private readonly ILspTransport _transport;
        private readonly ILspLogger _logger;

        public DiagnosticsPublisher(ILspServices lspServices, ILspTransport transport, ILspLogger logger)
        {
            _lspServices = lspServices;
            _transport = transport;
            _logger = logger;
        }

        async Task IDiagnosticsPublisher.ClearDiagnosticsAsync(Uri documentUri, CancellationToken cancellationToken)
        {
            var diag = new DiagnosticsParams
            {
                Uri = documentUri,
                Diagnostics = Array.Empty<Diagnostic>(),
            };
            var message = new LspJsonRpcMessage
            {
                Method = LspMethods.Diagnostics,
                Params = JsonSerializer.SerializeToElement(diag, Constants.DefaultSerializationOptions),
            };
            await _transport.SendAsync(message, cancellationToken);
        }

        async Task IDiagnosticsPublisher.PublishAllDiagnosticsAsync(RequestContext context, CancellationToken cancellationToken, bool logDiagnostics)
        {
            var savedFileUri = context.IsInvalid ? null : context.Document?.Uri;

            foreach (var documentDiagnostics in context.Workspace.GetDiagnostics(context))
            {
                var message = new LspJsonRpcMessage
                {
                    Method = LspMethods.Diagnostics,
                    Params = JsonSerializer.SerializeToElement(documentDiagnostics, Constants.DefaultSerializationOptions),
                };

                await _transport.SendAsync(message, cancellationToken);

                // Only log diagnostics for the file that triggered the save, not all workspace files.
                if (logDiagnostics && savedFileUri != null && documentDiagnostics.Uri == savedFileUri)
                {
                    var fileName = Path.GetFileName(documentDiagnostics.Uri.LocalPath);
                    foreach (var diagnostic in documentDiagnostics.Diagnostics)
                    {
                        var line = (diagnostic.Range?.Start.Line ?? 0) + 1;
                        var col = (diagnostic.Range?.Start.Character ?? 0) + 1;
                        if (diagnostic.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
                        {
                            _logger.LogTrace($"textDocument/publishDiagnostics: {fileName}(ln {line}, col {col}): {diagnostic.Message}");
                        }
                    }
                }
            }
        }

        async Task IDiagnosticsPublisher.PublishDiagnosticsForCurrentDocumentAsync<DocType>(RequestContext context, CancellationToken cancellationToken, bool logDiagnostics)
        {
            var diagnosticsProvider = _lspServices.GetService<IDiagnosticsProvider<DocType>>();
            if (diagnosticsProvider == null)
            {
                // could happen if document type doesn't have a Diagnostic provider registered (e.g. default implementation of change handler LspDocument)
                // this will be difficult to reproduce in production
                return;
            }

            var diagnostics = diagnosticsProvider.ComputeDiagnostics(context, context.Document.As<DocType>()).ToArray();

            var diag = new DiagnosticsParams
            {
                Uri = context.Document.Uri,
                Diagnostics = diagnostics,
            };
            var message = new LspJsonRpcMessage
            {
                Method = LspMethods.Diagnostics,
                Params = JsonSerializer.SerializeToElement(diag, Constants.DefaultSerializationOptions),
            };

            await _transport.SendAsync(message, cancellationToken);

            if (logDiagnostics)
            {
                var fileName = Path.GetFileName(context.Document.Uri.LocalPath);
                foreach (var diagnostic in diagnostics)
                {
                    var line = (diagnostic.Range?.Start.Line ?? 0) + 1;
                    var col = (diagnostic.Range?.Start.Character ?? 0) + 1;

                    switch (diagnostic.Severity)
                    {
                        case DiagnosticSeverity.Error:
                            _logger.LogTrace($"textDocument/publishDiagnostics: {fileName}(ln {line}, col {col}): {diagnostic.Message}");
                            break;
                        case DiagnosticSeverity.Warning:
                            _logger.LogTrace($"textDocument/publishDiagnostics: {fileName}(ln {line}, col {col}): {diagnostic.Message}");
                            break;
                    }
                }
            }
        }
    }
}
