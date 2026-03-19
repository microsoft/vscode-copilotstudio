namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Models
{
    public class CompletionParams : TextDocumentPositionParams
    {
        public CompletionContext? Context { get; init; } = null;
    }
}