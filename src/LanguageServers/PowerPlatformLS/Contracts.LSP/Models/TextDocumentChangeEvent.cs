namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Models
{
    public class TextDocumentChangeEvent
    {
        public string Text { get; set; } = string.Empty;

        public Range? Range { get; set; } = null;

        public int RangeLength { get; set; } = 0;
    }
}