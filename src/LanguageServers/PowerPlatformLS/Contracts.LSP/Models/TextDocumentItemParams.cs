namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Models
{
    using System.Text.Json.Serialization;

    public class TextDocumentItemParams
    {
        [JsonRequired]
        public required TextDocumentItem TextDocument { get; init; }
    }
}