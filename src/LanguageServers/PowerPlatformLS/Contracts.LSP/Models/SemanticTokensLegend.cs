namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Models
{
    public class SemanticTokensLegend
    {
        /// <summary>.
        /// The semantic token types the server uses. Indices into this array are used to encode token types in semantic tokens responses.
        /// </summary>        
        public string[]? TokenTypes { get; set; }

        /// <summary>
        /// The semantic token modifiers the server uses. Indices into this array are used to encode modifiers in semantic tokens responses.
        /// </summary>
        public string[]? TokenModifiers { get; set; }
    }
}