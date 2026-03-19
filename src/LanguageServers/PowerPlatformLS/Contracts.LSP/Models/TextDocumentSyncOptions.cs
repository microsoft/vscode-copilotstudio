namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Models
{
    public sealed class TextDocumentSyncOptions
    {
        public bool OpenClose { get; set; } = true;

        public TextDocumentSyncKind Change { get; set; } = TextDocumentSyncKind.Incremental;

        public SaveOptions? Save { get; set; }
    }
}