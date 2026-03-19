namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Models
{

    using System.Text.Json.Serialization;

    public sealed class LspCommand
    {
        public string Title { get; set; } = string.Empty;

        public string Command { get; set; } = string.Empty;

        public object[]? Arguments { get; set; } = null;
    }
}