namespace Microsoft.PowerPlatformLS.Impl.Core.Lsp.Uris
{
    /// <summary>
    /// Represents a URI with unsupported scheme or other validation failure.
    /// </summary>
    internal sealed class UnsupportedLspUri : LspUri
    {
        public UnsupportedLspUri(Uri uri, string scheme, string reasonCode) : base(uri, scheme)
        {
            ReasonCode = reasonCode ?? throw new ArgumentNullException(nameof(reasonCode));
        }

        /// <summary>
        /// Reason why this URI is unsupported (ParseError, NotAbsolute, UnsupportedScheme).
        /// </summary>
        public string ReasonCode { get; }

        /// <summary>
        /// Unsupported URIs are not supported for language operations.
        /// </summary>
        public override bool IsSupported => false;
    }

    /// <summary>
    /// Standard reason codes for unsupported URIs.
    /// </summary>
    internal static class UnsupportedReasonCodes
    {
        public const string ParseError = "ParseError";
        public const string NotAbsolute = "NotAbsolute";
        public const string UnsupportedScheme = "UnsupportedScheme";
    }
}
