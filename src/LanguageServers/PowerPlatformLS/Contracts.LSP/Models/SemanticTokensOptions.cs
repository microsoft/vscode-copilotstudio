namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Models
{
    public sealed class SemanticTokensOptions
    {
        /// <summary>
        /// Gets or sets a legend describing how semantic token types and modifiers are encoded in responses.
        /// </summary>
        public SemanticTokensLegend? Legend { get; set; }

        /// <summary>
        /// A document selector to identify the scope of the registration. If set to
        /// null the document selector provided on the client side will be used.
        /// </summary>
        public DocumentFilter[] DocumentSelector { get; set; } = [];

        /// <summary>
        /// Gets or sets a value indicating whether semantic tokens Range provider requests are supported.
        /// </summary>
        public bool Range { get; set; } = false;

        /// <summary>
        /// Gets or sets whether or not the server supports providing semantic tokens for a full document.
        /// </summary>
        public bool Full { get; set; } = true;
    }
}