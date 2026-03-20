namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Models
{
    using System.Text.Json.Serialization;
    
    /// <summary>
    /// Class which identifies a text document.
    ///
    /// See the <see href="https://microsoft.github.io/language-server-protocol/specifications/specification-current/#textDocumentIdentifier">Language Server Protocol specification</see> for additional information.
    /// </summary>
    public class TextDocumentIdentifier
    {
        [JsonRequired]
        public required Uri Uri { get; set; }
    }
}