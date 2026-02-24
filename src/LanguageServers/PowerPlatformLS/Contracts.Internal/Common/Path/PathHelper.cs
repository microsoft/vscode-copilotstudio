namespace Microsoft.PowerPlatformLS.Contracts.Internal.Common
{
    using System.Runtime.CompilerServices;

    /// <summary>
    /// Shared code for path types.
    /// </summary>
    internal static class PathHelper
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool AreEqual<T>(T path, object? obj, Func<T, string> getValue)
        {
            if (obj is T other)
            {
                return getValue(other).Equals(getValue(path), StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int GetHashCode<T>(T path, Func<T, string> getValue)
        {
            return getValue(path).GetHashCode(StringComparison.OrdinalIgnoreCase);
        }

        internal static T GetRelativeTo<T>(T path, DirectoryPath parent, Func<T, string> getValue, Func<string, T> create)
        {
            return create(getValue(path).Substring(parent.Length));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static string GetString<T>(T path, Func<T, string> getValue)
        {
            return getValue(path);
        }

        /// <summary>
        /// For usage before initialization.
        /// </summary>
        /// <param name="path">The path string. Must use forward slashes.</param>
        /// <exception cref="ArgumentException">Thrown if the path contains backslashes.</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void ValidatePath(string path)
        {
            if (path.IndexOf('\\') >= 0)
            {
                throw new ArgumentException($"Path should use forward slash: {path}", nameof(path));
            }
        }
    }
}