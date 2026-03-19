namespace Microsoft.PowerPlatformLS.Impl.Core.Lsp.Uris
{
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;

    /// <summary>
    /// Represents a file scheme URI (absolute only in Phase 1).
    /// </summary>
    internal sealed class FileLspUri : LspUri
    {
        public FileLspUri(Uri uri, string scheme) : base(uri, scheme)
        {
            if (!string.Equals(Scheme, "file", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"Expected file scheme, got '{Scheme}'", nameof(uri));
            }
        }

        /// <summary>
        /// File URIs are supported for language operations.
        /// </summary>
        public override bool IsSupported => true;

        /// <summary>
        /// Returns a FilePath using the existing normalization for compatibility.
        /// This bridges to existing workspace resolution logic.
        /// </summary>
        public FilePath AsFilePathNormalized()
        {
            return Uri.ToFilePath();
        }
    }
}
