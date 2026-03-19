namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Models
{
    public sealed class CodeActionParams : TextDocumentIdentifierParams
    {
        public required Range Range { get; set; }
        public required CodeActionContext Context { get; set; }
    }
}