namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Models
{
    using System.Text.Json.Serialization;

    public sealed class RenameParams : TextDocumentPositionParams
    {
        [JsonRequired]
        public required string NewName { get; init; }
    }
}
