namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Models
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// Class which represents a text document.
    ///
    /// See the <see href="https://microsoft.github.io/language-server-protocol/specifications/specification-current/#textDocumentItem">Language Server Protocol specification</see> for additional information.
    /// </summary>
    public sealed class TextDocumentItem
    {
        /// <summary>
        /// Gets or sets the document URI.
        /// </summary>
        [JsonRequired]
        public required Uri Uri { get; set; }

        public string LanguageId { get; set; } = string.Empty;

        public int Version { get; set; } = 0;

        public string Text { get; set; } = string.Empty;
    }
}