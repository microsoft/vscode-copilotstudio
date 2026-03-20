namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Models
{
    /// <summary>
    /// See https://microsoft.github.io/language-server-protocol/specifications/lsp/3.17/specification/#didChangeWatchedFilesParams
    /// </summary>
    public class DidChangeWatchedFilesParams : IDefaultContextRequest
    {
        public required FileEvent[] Changes { get; set; }
    }

    static public class DidChangeWatchedFilesParamsExtensions
    {
        /// <summary>
        /// Checks if the file rename should be ignored.
        /// This is to handle the case where a file is renamed with the same characters but different casing.
        /// Example: "file.mcs" to "File.mcs"
        /// </summary>
        /// <param name="request"></param>
        /// <param name="fileEvent"></param>
        /// <returns></returns>
        public static bool ShouldIgnore(this DidChangeWatchedFilesParams request, FileEvent fileEvent)
        {
            return request.Changes.Any(e => !StringComparer.Ordinal.Equals(e.Uri.AbsolutePath, fileEvent.Uri.AbsolutePath) &&
                                             StringComparer.OrdinalIgnoreCase.Equals(e.Uri.AbsolutePath, fileEvent.Uri.AbsolutePath));
        }
    }
}
