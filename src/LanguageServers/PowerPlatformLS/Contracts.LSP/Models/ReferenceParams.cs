namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Models
{
    using System.Text.Json.Serialization;

    public sealed class ReferenceParams : TextDocumentPositionParams
    {
        [JsonRequired]
        public required ReferenceContext Context { get; init; }
    }
}
