namespace Microsoft.PowerPlatformLS.Impl.Language.PowerFx.Framework
{
    using Microsoft.PowerFx;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common.Framework;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Impl.Language.PowerFx;
    using System.Collections.Generic;

    internal class DiagnosticsProvider : IDiagnosticsProvider<PowerFxLspDocument>
    {
        public IEnumerable<Diagnostic> ComputeDiagnostics(RequestContext requestContext, PowerFxLspDocument document)
        {
            var result = document.FileModel;
            if (result == null)
            {
                var diagnosis = new Diagnostic
                {
                    Range = new Contracts.Lsp.Models.Range(),
                    Severity = DiagnosticSeverity.Error,
                    Message = "Failed to compute semantic model."
                };
                return [diagnosis];
            }

            var errors = result.Errors;
            var diagnostics = errors.Select(error => new Diagnostic
            {
                Range = document.MarkResolver.GetRange(error.Span.Min, error.Span.Lim),
                Severity = DocumentSeverityToDiagnosticSeverityMap(error.Severity),
                Message = error.Message,
            });

            return diagnostics;
        }


        private static DiagnosticSeverity DocumentSeverityToDiagnosticSeverityMap(ErrorSeverity severity) => severity switch
        {
            ErrorSeverity.Critical => DiagnosticSeverity.Error,
            ErrorSeverity.Severe => DiagnosticSeverity.Error,
            ErrorSeverity.Moderate => DiagnosticSeverity.Error,
            ErrorSeverity.Warning => DiagnosticSeverity.Warning,
            ErrorSeverity.Suggestion => DiagnosticSeverity.Hint,
            ErrorSeverity.Verbose => DiagnosticSeverity.Information,
            _ => DiagnosticSeverity.Information
        };
    }
}
