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

        public DiagnosticsPublisher(ILspServices lspServices, ILspTransport transport)
        {
            _lspServices = lspServices;
            _transport = transport;
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

        async Task IDiagnosticsPublisher.PublishAllDiagnosticsAsync(RequestContext context, CancellationToken cancellationToken)
        {
            foreach (var documentDiagnostics in context.Workspace.GetDiagnostics(context))
            {
                var message = new LspJsonRpcMessage
                {
                    Method = LspMethods.Diagnostics,
                    Params = JsonSerializer.SerializeToElement(documentDiagnostics, Constants.DefaultSerializationOptions),
                };

                await _transport.SendAsync(message, cancellationToken);
            }
        }

        async Task IDiagnosticsPublisher.PublishDiagnosticsForCurrentDocumentAsync<DocType>(RequestContext context, CancellationToken cancellationToken)
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
        }
    }
}
