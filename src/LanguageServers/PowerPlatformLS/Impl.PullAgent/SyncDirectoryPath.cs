namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;

    /// <summary>
    /// Converts between the extension's DirectoryPath (Contracts.Internal.Common)
    /// and the shared library's DirectoryPath (CopilotStudio.Sync).
    /// Both are structurally identical readonly structs wrapping normalized path strings.
    /// </summary>
    internal static class SyncDirectoryPath
    {
        public static CopilotStudio.Sync.DirectoryPath ToSync(this DirectoryPath path)
            => new CopilotStudio.Sync.DirectoryPath(path.ToString());

        public static DirectoryPath ToContracts(this CopilotStudio.Sync.DirectoryPath path)
            => new DirectoryPath(path.ToString());
    }
}
