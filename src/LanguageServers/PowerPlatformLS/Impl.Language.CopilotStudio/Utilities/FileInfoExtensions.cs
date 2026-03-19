namespace Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Utilities
{
    using Microsoft.Extensions.FileProviders;
    using Microsoft.PowerPlatformLS.Contracts.FileLayout;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using System;

    internal static class FileInfoExtensions
    {
        public static AgentFilePath ToAgentFilePath(this IFileInfo fileInfo, DirectoryPath workspacePath)
        {
            var filePath = fileInfo.ToFilePath();
            return new AgentFilePath(filePath.GetRelativeTo(workspacePath));
        }

        public static FilePath ToFilePath(this IFileInfo fileInfo)
        {
            return ToPath(fileInfo, p => new FilePath(p));
        }

        public static DirectoryPath ToDirectoryPath(this IFileInfo fileInfo)
        {
            return ToPath(fileInfo, p => new DirectoryPath(p));
        }

        private static TPath ToPath<TPath>(IFileInfo fileInfo, Func<string, TPath> ctor)
        {
            if (fileInfo.PhysicalPath == null)
            {
                throw new ArgumentException("File doesn't have a physical path", nameof(fileInfo));
            }

            return ctor(fileInfo.PhysicalPath.Replace('\\', '/'));
        }
    }
}
