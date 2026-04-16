

namespace Microsoft.PowerPlatformLS.Contracts.Internal.Models
{
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using System.Diagnostics;

    public static class UriExtensions
    {
        private static string ToNormalizedPath(this Uri uri)
        {
            // Linux/Osx specific path normalization. LocalPath is already unescaped.
            if (PlatformService.IsUnix() && uri.LocalPath.StartsWith('/') && uri.Segments.Length > 1)
            {
                return uri.LocalPath;
            }

            return uri.GetComponents(UriComponents.Path, UriFormat.Unescaped);
        }

        public static DirectoryPath ToDirectoryPath(this Uri uri)
        {
            var normalizedPath = uri.ToNormalizedPath();
            Debug.Assert(normalizedPath == string.Empty || Path.IsPathRooted(normalizedPath), "LSP methods should provide absolute URI.");
            return new DirectoryPath(normalizedPath);
        }

        public static FilePath ToFilePath(this Uri uri)
        {
            var normalizedPath = uri.ToNormalizedPath();
            Debug.Assert(Path.IsPathRooted(normalizedPath), "LSP methods should provide absolute URI.");
            return new FilePath(normalizedPath);
        }
    }
}