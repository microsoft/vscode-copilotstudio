namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Models
{
    public sealed class TextDocumentEdit : IFileOperation
    {
        public const string KindName = "edit";
        public string Kind { get; set; } = KindName;
        public required VersionedTextDocumentIdentifier TextDocument { get; set; }
        public required TextEdit[] Edits { get; set; }
    }
}