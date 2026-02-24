namespace Microsoft.PowerPlatformLS.Contracts.Internal.Common
{
    using Microsoft.Extensions.FileProviders;

    public interface IClientWorkspaceFileProvider
    {
        /// <summary>
        /// Locate a file at the given path.
        /// </summary>
        /// <param name="path">Path that identifies the file.</param>
        /// <returns>The file information. Caller must check Exists property.</returns>
        IFileInfo GetFileInfo(FilePath path);

        /// <summary>
        /// Locate a directory at the given path.
        /// </summary>
        /// <param name="path">Path that identifies the directory.</param>
        /// <returns>The directory file information. Caller must check Exists property.</returns>
        IFileInfo GetFileInfo(DirectoryPath path);

        /// <summary>
        /// Enumerate a directory at the given path, if any.
        /// </summary>
        /// <param name="path">The path that identifies the directory.</param>
        /// <returns>The contents of the directory.</returns>
        IDirectoryContents GetDirectoryContents(DirectoryPath path);
    }
}
