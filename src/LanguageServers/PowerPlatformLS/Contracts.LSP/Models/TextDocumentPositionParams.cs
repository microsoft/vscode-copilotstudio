namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Models
{
    using System.Text.Json.Serialization;

    public class TextDocumentPositionParams
    {
        [JsonRequired]
        public required TextDocumentIdentifier TextDocument { get; init; }

        [JsonRequired]
        public Position Position { get; init; } = new();
    }
}