namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Models
{
    public sealed class CodeAction
    {
        public required string Title { get; set; }
        public required string Kind { get; set; }
        public Diagnostic[]? Diagnostics { get; set; } = null;
        public bool IsPreferred { get; set; } = false;
        public WorkspaceEdit? Edit { get; set; }
    }
}