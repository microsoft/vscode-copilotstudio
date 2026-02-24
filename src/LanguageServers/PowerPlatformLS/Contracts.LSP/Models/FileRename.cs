namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Models
{
    public sealed class FileRename
    {
        public required Uri OldUri { get; set; }
        public required Uri NewUri { get; set; }
    }

    static public class FileRenameExtensions
    {
        /// <summary>
        /// Checks if the file rename should be ignored.
        /// This is to handle the case where a file is renamed with the same characters but different casing.
        /// Example: "file.mcs" to "File.mcs"
        /// </summary>
        /// <param name="fileRename"></param>
        /// <returns></returns>
        public static bool ShouldIgnore(this FileRename fileRename)
        {
            return StringComparer.OrdinalIgnoreCase.Equals(fileRename.OldUri.AbsolutePath, fileRename.NewUri.AbsolutePath);
        }
    }
}