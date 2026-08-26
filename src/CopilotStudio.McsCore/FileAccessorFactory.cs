// Copyright (C) Microsoft Corporation. All rights reserved.
// Ported from om/src/vscode/LanguageServers/PowerPlatformLS/Impl.PullAgent/File/FileWriter.cs


namespace Microsoft.CopilotStudio.McsCore;

internal class FileAccessorFactory : IFileAccessorFactory
{
    public bool IsMemoryBacked => false;

    public IFileAccessor Create(DirectoryPath root) => new FileWriter(root);

    public void Release(DirectoryPath root)
    {
    }

    private class FileWriter : IFileAccessor
    {
        private const string ReplaceBackupSuffix = ".replace.bak";

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
            return new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 81920, useAsync: true);
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

        public void DeleteDirectory(AgentFilePath path)
        {
            var fullPath = _root.GetChildDirectoryPath(path.ToString()).ToString();
            if (Directory.Exists(fullPath))
            {
                Directory.Delete(fullPath, recursive: true);
            }
        }

        public bool Exists(AgentFilePath path) => File.Exists(FullPath(path).ToString());

        public Stream OpenRead(AgentFilePath path)
        {
            try
            {
                return new FileStream(FullPath(path).ToString(), FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: true);
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

            if (!File.Exists(targetFullPath))
            {
                File.Move(sourceFullPath, targetFullPath);
                return;
            }

            var backupFullPath = targetFullPath + ReplaceBackupSuffix;
            var retainBackup = false;
            try
            {
                try
                {
                    File.Replace(sourceFullPath, targetFullPath, backupFullPath, ignoreMetadataErrors: true);
                }
                catch (Exception replaceFailure) when (replaceFailure is PlatformNotSupportedException or IOException)
                {
                    if (!File.Exists(targetFullPath) && File.Exists(backupFullPath))
                    {
                        try
                        {
                            File.Move(backupFullPath, targetFullPath);
                        }
                        catch (Exception restoreFailure)
                        {
                            throw new ReplaceRecoveryException(
                                "Replacing a file failed and its original content could not be restored. A copy of the original content is retained alongside it with the .replace.bak extension.",
                                restoreFailure);
                        }

                        throw;
                    }

                    ReplaceByCopy(sourceFullPath, targetFullPath, backupFullPath);
                }
            }
            catch (ReplaceRecoveryException)
            {
                retainBackup = true;
                throw;
            }
            finally
            {
                if (!retainBackup)
                {
                    TryFileOperation(() => File.Delete(backupFullPath));
                }
            }
        }

        private static void ReplaceByCopy(string sourceFullPath, string targetFullPath, string backupFullPath)
        {
            File.Copy(targetFullPath, backupFullPath, overwrite: true);
            try
            {
                File.Copy(sourceFullPath, targetFullPath, overwrite: true);
                File.Delete(sourceFullPath);
            }
            catch
            {
                try
                {
                    File.Copy(backupFullPath, targetFullPath, overwrite: true);
                }
                catch (Exception restoreFailure)
                {
                    throw new ReplaceRecoveryException(
                        "Replacing a file failed and its original content could not be restored. A copy of the original content is retained alongside it with the .replace.bak extension.",
                        restoreFailure);
                }

                throw;
            }
        }

        private sealed class ReplaceRecoveryException : IOException
        {
            public ReplaceRecoveryException(string message, Exception innerException)
                : base(message, innerException)
            {
            }
        }

        private static void TryFileOperation(Action fileOperation)
        {
            try
            {
                fileOperation();
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or FileNotFoundException or DirectoryNotFoundException)
            {
            }
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
                var relative = PathHelper.GetRelativePath(rootPath, file).Replace('\\', '/');
                yield return new AgentFilePath(relative);
            }
        }
    }
}
