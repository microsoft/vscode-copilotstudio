namespace Microsoft.PowerPlatformLS.Contracts.Internal.Models.Lsp
{
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using System.Diagnostics.CodeAnalysis;
    using System.Globalization;

    public interface IClientInformation
    {
        CultureInfo CultureInfo { get; }
        InitializeParams InitializeParams { get; }

        /// <summary>
        /// Given a directory path, returns the client workspace folder path that contains the file, if any.
        /// Returns failse otherwise.
        /// </summary>
        bool TryGetWorkspaceFolder(DirectoryPath directoryPath, [MaybeNullWhen(false)] out DirectoryPath clientWorkspaceFolder);

        /// <summary>
        /// Given an agent directory path, returns a path relative to a client workspace folder, if any contains the agent directory.
        /// Returns the original path otherwise.
        /// </summary>
        DirectoryPath GetRelativePath(DirectoryPath agentDirectoryPath)
        {
            if (TryGetWorkspaceFolder(agentDirectoryPath, out var clientWorkspaceFolderPath))
            {
                return agentDirectoryPath.GetRelativeTo(clientWorkspaceFolderPath);
            }

            return agentDirectoryPath;
        }
    }
}
