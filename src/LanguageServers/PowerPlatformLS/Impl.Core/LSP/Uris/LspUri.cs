namespace Microsoft.PowerPlatformLS.Impl.Core.Lsp.Uris
{
    /// <summary>
    /// Abstract base class for typed URI representations in LSP context.
    /// Preserves raw identity and provides scheme-specific equality semantics.
    /// </summary>
    internal abstract class LspUri
    {
        protected LspUri(Uri uri, string scheme)
        {
            Uri = uri ?? throw new ArgumentNullException(nameof(uri));
            Scheme = scheme ?? throw new ArgumentNullException(nameof(scheme));
        }

        /// <summary>
        /// The underlying URI object, always absolute.
        /// </summary>
        public Uri Uri { get; }

        /// <summary>
        /// The scheme of this URI (e.g., "file", "http").
        /// </summary>
        public string Scheme { get; }

        /// <summary>
        /// Original string for emission and comparison.
        /// </summary>
        public string Raw => Uri.OriginalString;

        /// <summary>
        /// Whether this URI type is supported for language operations.
        /// </summary>
        public abstract bool IsSupported { get; }

        /// <summary>
        /// JSON emission representation. Phase 1: always returns Raw (absolute).
        /// </summary>
        public virtual string ToJsonUri() => Raw;
    }
}
