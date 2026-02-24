namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.PowerPlatformLS.Contracts.FileLayout;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using System.IO;

    internal class FileAccessorFactory : IFileAccessorFactory
    {
        public IFileAccessor Create(DirectoryPath root) => new FileWriter(root);

        // Implementation to write to disk. 
        private class FileWriter : IFileAccessor
        {
            private readonly DirectoryPath _root;

            public FileWriter(DirectoryPath root)
            {
                _root = root;
            }

            public Stream OpenWrite(AgentFilePath path)
            {
                string fullPath = FullPath(path).ToString();
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
                    string fullPath = FullPath(path).ToString();
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
                    // Normalize to FileNotFoundException
                    throw new FileNotFoundException(e.Message);
                }
            }

            private FilePath FullPath(AgentFilePath path) => _root.GetChildFilePath(path.ToString());

            public void CreateHiddenDirectory(AgentFilePath path)
            {
                var di = Directory.CreateDirectory(_root.GetChildDirectoryPath(path.ToString()).ToString());
                di.Attributes = FileAttributes.Directory | FileAttributes.Hidden;
            }

            // Replace file content from sourcePath to targetPath.
            public void Replace(AgentFilePath sourcePath, AgentFilePath targetPath)
            {
                string sourceFullPath = FullPath(sourcePath).ToString();
                string targetFullPath = FullPath(targetPath).ToString();

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
        }
    }
}
