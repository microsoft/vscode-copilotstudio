namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Models
{
    public sealed class DiagnosticData
    {
        public CodeAction[]? Quickfix { get; set; } = null;
    }
}
