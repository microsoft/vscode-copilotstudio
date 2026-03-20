namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Models
{
    using System.Text.Json.Serialization;

    public sealed class ClientInfo
    {
        public string Name { get; set; } = string.Empty;

        public string? Version { get; set; } = string.Empty;
    }
}