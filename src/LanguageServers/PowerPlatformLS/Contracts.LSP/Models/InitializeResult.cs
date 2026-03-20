namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Models
{

    using System.Text.Json.Serialization;

    public sealed class InitializeResult
    {
        public required ServerCapabilities Capabilities { get; set; }

        public LspServerInfo? ServerInfo { get; set; } = null;
    }
}