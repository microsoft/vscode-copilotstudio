namespace Microsoft.PowerPlatformLS.Contracts.Internal.Models.Lsp
{
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;

    public enum ParsingStatus
    {
        NotParsed,
        Success,
        Error,
        Warning,
    }

    public class ParsingResult
    {
        public Diagnostic? Diagnostic { get; set; }

        public bool HasError => Diagnostic?.Severity == DiagnosticSeverity.Error;
    }
}