namespace Microsoft.PowerPlatformLS.UnitTests.TestUtilities
{
    using Microsoft.PowerPlatformLS.Contracts.FileLayout;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using Microsoft.PowerPlatformLS.Impl.PullAgent;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using Microsoft.CopilotStudio.McsCore;


    // Test helper. Write to in-memory instead of disk.
    // Useful when we need to track writers across multiple workspaces.
    internal class InMemoryFileAccessorFactory :
        IFileAccessorFactory,
        Microsoft.CopilotStudio.Sync.IFileAccessorFactory
    {
        private readonly Dictionary<DirectoryPath, InMemoryFileWriter> _writers = new Dictionary<DirectoryPath, InMemoryFileWriter>();

        public IReadOnlyDictionary<DirectoryPath, InMemoryFileWriter> Writers => _writers;

        public IFileAccessor Create(DirectoryPath root)
        {
            if (_writers.TryGetValue(root, out var writer))
            {
                return writer;
            }
            writer = new InMemoryFileWriter();
            _writers.Add(root, writer);
            return writer;
        }

        Microsoft.CopilotStudio.Sync.IFileAccessor Microsoft.CopilotStudio.Sync.IFileAccessorFactory.Create(DirectoryPath root)
        {
            var contractsRoot = new DirectoryPath(root.ToString());
            return (InMemoryFileWriter)Create(contractsRoot);
        }
    }

    // Test helper. Write to in-memory instead of disk.
    // Tracks writes within a single workspace.
    internal class InMemoryFileWriter :
        IFileAccessorFactory,
        IFileAccessor,
        Microsoft.CopilotStudio.Sync.IFileAccessorFactory,
        Microsoft.CopilotStudio.Sync.IFileAccessor
    {
        private readonly Dictionary<AgentFilePath, byte[]> _files = new Dictionary<AgentFilePath, byte[]>();

        private readonly HashSet<AgentFilePath> _inUse = new HashSet<AgentFilePath>();

        private void Lock(AgentFilePath path)
        {
            if (!_inUse.Add(path))
            {
                throw new System.IO.IOException($"{path} locked.");
            }
        }
        private void Unlock(AgentFilePath path)
        {
            _inUse.Remove(path);
        }

        public string[] Filenames => _files.Keys.Select(x => x.ToString()).Order().ToArray();

        public void Delete(AgentFilePath path)
        {
            _files.Remove(path);
        }

        public bool Exists(AgentFilePath path)
        {
            return _files.ContainsKey(path);
        }

        public Stream OpenRead(AgentFilePath path)
        {
            Lock(path);
            if (_files.TryGetValue(path, out var bytes))
            {
                var stream = new ReadWrapper(bytes)
                {
                    OnClose = () => {
                        Unlock(path);
                    }
                };
                return stream;
            }
            throw new FileNotFoundException(path.ToString());
        }

        public Stream OpenWrite(AgentFilePath path)
        {
            Lock(path);
            var stream = new WriteWrapper
            {
                OnClose = (bytes) => {
                    _files[path] = bytes;
                    Unlock(path);
                }
            };

            return stream;
        }

        public void CreateHiddenDirectory(AgentFilePath path)
        {
        }

        /// <summary>
        /// Simulates replacing a target file with a source file in memory.
        /// </summary>
        public void Replace(AgentFilePath sourcePath, AgentFilePath targetPath)
        {
            if (!_files.TryGetValue(sourcePath, out var tmpBytes))
            {
                throw new FileNotFoundException($"Temp file not found: {sourcePath}");
            }

            _files[targetPath] = tmpBytes;
            _files.Remove(sourcePath);
        }

        public IFileAccessor Create(DirectoryPath root) => this;

        // CopilotStudio.Sync.IFileAccessor implementation (delegates to Contracts.FileLayout methods via string conversion)
        bool Microsoft.CopilotStudio.Sync.IFileAccessor.Exists(AgentFilePath path) => Exists(new AgentFilePath(path.ToString()));
        void Microsoft.CopilotStudio.Sync.IFileAccessor.CreateHiddenDirectory(AgentFilePath path) => CreateHiddenDirectory(new AgentFilePath(path.ToString()));
        Stream Microsoft.CopilotStudio.Sync.IFileAccessor.OpenWrite(AgentFilePath path) => OpenWrite(new AgentFilePath(path.ToString()));
        Stream Microsoft.CopilotStudio.Sync.IFileAccessor.OpenRead(AgentFilePath path) => OpenRead(new AgentFilePath(path.ToString()));
        void Microsoft.CopilotStudio.Sync.IFileAccessor.Delete(AgentFilePath path) => Delete(new AgentFilePath(path.ToString()));
        void Microsoft.CopilotStudio.Sync.IFileAccessor.Replace(AgentFilePath sourcePath, AgentFilePath targetPath) => Replace(new AgentFilePath(sourcePath.ToString()), new AgentFilePath(targetPath.ToString()));
        IEnumerable<AgentFilePath> Microsoft.CopilotStudio.Sync.IFileAccessor.ListFiles(string? relativeFolder, string filePattern) =>
            _files.Keys
                .Where(k => relativeFolder == null || k.ToString().StartsWith(relativeFolder, StringComparison.OrdinalIgnoreCase))
                .Select(k => new AgentFilePath(k.ToString()));

        // CopilotStudio.Sync.IFileAccessorFactory implementation
        Microsoft.CopilotStudio.Sync.IFileAccessor Microsoft.CopilotStudio.Sync.IFileAccessorFactory.Create(DirectoryPath root) => this;

        private class ReadWrapper : MemoryStream
        {
            public required Action OnClose { get; set; }

            public ReadWrapper(byte[] bytes) : base(bytes)
            {
            }

            public override bool CanSeek => false;

            protected override void Dispose(bool disposing)
            {
                OnClose();
                base.Dispose(disposing);
            }
        }

        private class WriteWrapper : MemoryStream
        {
            public required Action<byte[]> OnClose { get; set; }

            public WriteWrapper() : base()
            {
            }

            public override bool CanRead => false;
            public override bool CanSeek => false;

            protected override void Dispose(bool disposing)
            {
                var bytes = ToArray();
                OnClose(bytes);
                base.Dispose(disposing);
            }
        }
    }
}
