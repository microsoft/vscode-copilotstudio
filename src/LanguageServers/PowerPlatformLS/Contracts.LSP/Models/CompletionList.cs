namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Models
{
    using System.Text.Json.Serialization;

    public sealed class CompletionList
    {
        public CompletionItem[] Items { get; set; } = Array.Empty<CompletionItem>();

        public bool IsIncomplete { get; set; } = false;

        public static readonly CompletionList Empty = new() { Items = [], IsIncomplete = false };
    }
}