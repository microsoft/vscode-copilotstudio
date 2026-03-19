namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Models
{
    /// <summary>
    /// Class representing response to semantic tokens messages.
    /// <para>
    /// See the <see href="https://microsoft.github.io/language-server-protocol/specifications/specification-current/#semanticTokens">Language Server Protocol specification</see> for additional information.
    /// </para>
    /// </summary>
    public class SemanticTokens
    {
        /// <summary>
        /// An optional result id.
        /// <para>
        /// If provided and clients support delta updating the client will include the
        /// result id in the next semantic token request. A server can then instead of
        /// computing all semantic tokens again simply send a delta.
        /// </para>
        /// </summary>
        public string? ResultId { get; set; }

        /// <summary>
        /// Gets or sets and array containing encoded semantic tokens data.
        /// https://microsoft.github.io/language-server-protocol/specifications/lsp/3.17/specification/#textDocument_semanticTokens
        /// </summary>
        public int[]? Data { get; set; } = null;
    }
}