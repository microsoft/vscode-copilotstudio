namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Models
{
    using System.Text.Json.Serialization;

    public sealed class CompletionItem
    {
        public string Label { get; set; } = string.Empty;

        public CompletionItemLabelDetails? LabelDetails { get; set; } = null;

        public CompletionKind? Kind { get; set; } = null;

        public CompletionItemTag[]? Tags { get; set; } = null;

        public string? Detail { get; set; } = null;

        public string? Documentation { get; set; } = null;

        public string? SortText { get; set; } = null;

        public bool? Deprecated { get; set; } = null;

        public bool? Preselect { get; set; } = null;

        public string? FilterText { get; set; } = null;

        public string? InsertText { get; set; } = null;

        public InsertTextFormat? InsertTextFormat { get; set; } = null;

        public InsertTextMode? InsertTextMode { get; set; } = null;

        public TextEdit? TextEdit { get; set; } = null;

        public string? TextEditText { get; set; } = null;

        public TextEdit[]? AdditionalTextEdits { get; set; } = null;

        public string[]? CommitCharacters { get; set; } = null;

        public LspCommand? Command { get; set; } = null;

        public object? Data { get; set; } = null;
    }
}
