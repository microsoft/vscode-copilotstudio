namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Models
{
    public sealed class VersionedTextDocumentIdentifier : TextDocumentIdentifier
    {
        public int Version { get; set; } = 0;
    }
}