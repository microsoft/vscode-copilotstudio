namespace Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Exceptions
{
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models.Lsp;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;

    internal abstract class McsException : Exception
    {
        private readonly DiagnosticSeverity _diagnosticSeverity;

        public McsException(string? message, DiagnosticSeverity diagnosticSeverity = DiagnosticSeverity.Error) : base(message)
        {
            _diagnosticSeverity = diagnosticSeverity;
        }

        public Diagnostic GetDiagnostic(LspDocument document, RequestContext context)
        {
            var diagnosticData = GetDiagnosticData(document, context);
            return new Diagnostic
            {
                Message = Message,
                Severity = _diagnosticSeverity,
                Range = Range.Zero,
                Data = GetDiagnosticData(document, context),
            };
        }

        protected abstract DiagnosticData GetDiagnosticData(LspDocument document, RequestContext context);
    }
}
