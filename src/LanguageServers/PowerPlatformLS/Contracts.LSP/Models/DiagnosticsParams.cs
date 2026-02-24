namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Models
{
    using System.Text.Json.Serialization;

    public class DiagnosticsParams
    {
        public required Uri Uri { get; set; }

        public int Version { get; set; } = 0;

        public Diagnostic[] Diagnostics { get; set; } = [];
    }
}