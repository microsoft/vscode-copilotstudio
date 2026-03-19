namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Models
{
    public sealed class OnDidChangeParams : VersionedTextDocumentIdentifierParams
    {
        public TextDocumentChangeEvent[] ContentChanges { get; set; } = [];
    }
}