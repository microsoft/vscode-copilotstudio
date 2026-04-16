// Copyright (C) Microsoft Corporation. All rights reserved.
// Ported from om/src/vscode/LanguageServers/PowerPlatformLS/Impl.PullAgent/File/FileWriter.cs

using Microsoft.CopilotStudio.McsCore;

namespace Microsoft.CopilotStudio.Sync;

internal class FileAccessorFactory : IFileAccessorFactory
{
    public IFileAccessor Create(DirectoryPath root) => new FileWriter(root);

    private class FileWriter : IFileAccessor
    {
        private readonly DirectoryPath _root;

        public FileWriter(DirectoryPath root)
        {
            _root = root;
        }

        public Stream OpenWrite(AgentFilePath path)
        {
            var fullPath = FullPath(path).ToString();
            var dir = Path.GetDirectoryName(fullPath);
            if (dir == null)
            {
                throw new FileNotFoundException("Could not resolve directory for file " + fullPath);
            }

            Directory.CreateDirectory(dir);
            var stream = File.Create(fullPath);
            return stream;
        }

        public void Delete(AgentFilePath path)
        {
            try
            {
                var fullPath = FullPath(path).ToString();
                File.Delete(fullPath);
            }
            catch (DirectoryNotFoundException)
            {
                // if dir doesn't exist, then file doesn't.
            }
        }

        public bool Exists(AgentFilePath path) => File.Exists(FullPath(path).ToString());

        public Stream OpenRead(AgentFilePath path)
        {
            try
            {
                return File.Open(FullPath(path).ToString(), FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            catch (DirectoryNotFoundException e)
            {
                throw new FileNotFoundException(e.Message);
            }
        }

        private FilePath FullPath(AgentFilePath path) => _root.GetChildFilePath(path.ToString());

        public void CreateHiddenDirectory(AgentFilePath path)
        {
            var di = Directory.CreateDirectory(_root.GetChildDirectoryPath(path.ToString()).ToString());
            di.Attributes = FileAttributes.Directory | FileAttributes.Hidden;
        }

        public void Replace(AgentFilePath sourcePath, AgentFilePath targetPath)
        {
            var sourceFullPath = FullPath(sourcePath).ToString();
            var targetFullPath = FullPath(targetPath).ToString();

            var directoryName = Path.GetDirectoryName(targetFullPath);
            if (directoryName != null)
            {
                Directory.CreateDirectory(directoryName);
            }

            if (File.Exists(targetFullPath))
            {
                File.Delete(targetFullPath);
            }

            File.Move(sourceFullPath, targetFullPath);
        }

        public IEnumerable<AgentFilePath> ListFiles(string? relativeFolder = null, string filePattern = "*.*")
        {
            var rootPath = _root.ToString();
            relativeFolder = relativeFolder?.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            var fullSearchPath = string.IsNullOrEmpty(relativeFolder) ? rootPath : Path.Combine(rootPath, relativeFolder);

            if (!Directory.Exists(fullSearchPath))
            {
                yield break;
            }

            foreach (var file in Directory.EnumerateFiles(fullSearchPath, filePattern, SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(rootPath, file).Replace('\\', '/');
                yield return new AgentFilePath(relative);
            }
        }
    }
}
