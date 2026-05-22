

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

        /// <summary>
        /// Checks whether a path is rooted, recognizing both Unix absolute paths
        /// and Windows drive-letter paths (e.g. "C:/...") regardless of the host OS.
        /// </summary>
        private static bool IsAbsolutePath(string path)
        {
            return Path.IsPathRooted(path) ||
                   (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':');
        }

        public static DirectoryPath ToDirectoryPath(this Uri uri)
        {
            var normalizedPath = uri.ToNormalizedPath();
            Debug.Assert(normalizedPath == string.Empty || IsAbsolutePath(normalizedPath), "LSP methods should provide absolute URI.");
            return new DirectoryPath(normalizedPath);
        }

        public static FilePath ToFilePath(this Uri uri)
        {
            var normalizedPath = uri.ToNormalizedPath();
            Debug.Assert(IsAbsolutePath(normalizedPath), "LSP methods should provide absolute URI.");
            return new FilePath(normalizedPath);
        }
    }
}