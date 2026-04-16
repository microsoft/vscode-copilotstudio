namespace Microsoft.PowerPlatformLS.Impl.Core.IO
{
    using Microsoft.Extensions.FileProviders;
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models.Lsp;
    using System;

    /// <summary>
    /// Wrapper on a <see cref="IFileProvider"/> collection that allows to read files from the client workspace roots.
    /// </summary>
    internal class ClientWorkspaceFileProvider : IClientWorkspaceFileProvider
    {
        private readonly IClientInformation _clientInfo;
        private readonly IFileProviderFactory _factory;
        private readonly Dictionary<DirectoryPath, IFileProvider> _fileProviders = new();

        public ClientWorkspaceFileProvider(IClientInformation clientInfo, IFileProviderFactory factory)
        {
            _clientInfo = clientInfo;
            _factory = factory;
        }

        public IDirectoryContents GetDirectoryContents(DirectoryPath path)
        {
            var (fileProvider, clientWorkspaceFolder) = GetFileProvider(path);
            var relativePath = path.GetRelativeTo(clientWorkspaceFolder);
            return fileProvider.GetDirectoryContents(relativePath.ToString());
        }

        public IFileInfo GetFileInfo(DirectoryPath path)
        {
            return GetFileInfo(
                path,
                p => p,
                (p, clientWorkspaceFolder) => p.GetRelativeTo(clientWorkspaceFolder).ToString());
        }

        public IFileInfo GetFileInfo(FilePath path)
        {
            return GetFileInfo(
                path,
                p => p.ParentDirectoryPath,
                (p, clientWorkspaceFolder) => p.GetRelativeTo(clientWorkspaceFolder).ToString());
        }

        private IFileInfo GetFileInfo<T>(T path, Func<T, DirectoryPath> dirFunc, Func<T, DirectoryPath, string> getRelativePath)
        {
            var (fileProvider, clientWorkspaceFolder) = GetFileProvider(dirFunc(path));
            var relativePath = getRelativePath(path, clientWorkspaceFolder);
            return fileProvider.GetFileInfo(relativePath.ToString());
        }

        private (IFileProvider, DirectoryPath) GetFileProvider(DirectoryPath directoryPath)
        {
            if (_clientInfo.TryGetWorkspaceFolder(directoryPath, out var workspaceFolderPath))
            {
                if (!_fileProviders.ContainsKey(workspaceFolderPath))
                {
                    var clientWorkspaceRootPath = workspaceFolderPath.ToString();
                    _fileProviders[workspaceFolderPath] = _factory.Create(string.IsNullOrEmpty(clientWorkspaceRootPath) ? "/" : clientWorkspaceRootPath);
                }

                return (_fileProviders[workspaceFolderPath], workspaceFolderPath);
            }
            else
            {
                throw new InvalidOperationException("Can't read file outside of client workspace");
            }
        }
    }
}
