namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Models
{
    using System.Text.Json.Serialization;

    public class VersionedTextDocumentIdentifierParams
    {
        [JsonRequired]
        public required VersionedTextDocumentIdentifier TextDocument { get; init; }
    }
}