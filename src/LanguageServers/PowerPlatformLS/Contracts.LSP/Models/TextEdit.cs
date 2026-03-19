namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Models
{
    public sealed class TextEdit
    {
        public Range Range { get; set; } = new Range();

        public string NewText { get; set; } = string.Empty;
    }
}