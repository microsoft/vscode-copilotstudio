namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Models
{

    using System.Text.Json.Serialization;

    public sealed class CompletionItemLabelDetails
    {
        public string? Detail { get; set; }

        public string? Description { get; set; }

    }
}