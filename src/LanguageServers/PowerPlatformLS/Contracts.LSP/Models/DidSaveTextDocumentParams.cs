namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Models
{
    /// <summary>
    /// See https://microsoft.github.io/language-server-protocol/specifications/lsp/3.17/specification/#didSaveTextDocumentParams
    /// </summary>
    public class DidSaveTextDocumentParams : TextDocumentIdentifierParams
    {
        /// <summary>
        /// (Optional) the content when saved. Depends on the includeText value
        /// when the save notification was requested.
        /// </summary>
        public string? Text { get; set; }
    }
}
