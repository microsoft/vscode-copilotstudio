namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Models
{
    using System.Text.Json;

    /// <summary>
    /// See https://microsoft.github.io/language-server-protocol/specifications/lsp/3.17/specification/#diagnostic
    /// </summary>
    public class Diagnostic
    {
        public string? Code { get; set; }

        public Range? Range { get; set; }

        public DiagnosticSeverity Severity { get; set; }

        public string Message { get; set; } = string.Empty;

        /// <summary>
        /// A data entry field that is preserved between a
        /// `textDocument/publishDiagnostics` notification and
        /// `textDocument/codeAction` request.
        ///
        /// Internal PowerPlatformLS convention is to set the CodeAction here during `publishDiagnostics`,
        /// and sort and confirm the right action(s) during the `codeAction` request.
        /// </summary>
        public DiagnosticData? Data { get; set; }
    }

    public enum DiagnosticSeverity
    {
        Error = 1,
        Warning = 2,
        Information = 3,
        Hint = 4
    }
}