namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Models
{
    public sealed class LspServerInfo
    {
        public string Name { get; set; } = string.Empty;

        public string? Version { get; set; }
    }
}