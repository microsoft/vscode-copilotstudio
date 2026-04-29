namespace Microsoft.PowerPlatformLS.UnitTests.Impl.Core
{
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.Extensions.FileProviders;
    using Microsoft.Extensions.Primitives;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models.Lsp;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Impl.Core.IO;
    using Microsoft.PowerPlatformLS.Impl.Core.Lsp;
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Diagnostics.CodeAnalysis;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using Xunit;

    public class ClientWorkspaceFileProviderTests
    {
        // Probe test: reproduces issue #156 — concurrent GetFileInfo calls corrupt the
        // plain Dictionary<DirectoryPath, IFileProvider> in ClientWorkspaceFileProvider.
        // Expected to FAIL before the fix and PASS after.
        [Fact]
        public void GetFileInfo_ConcurrentCalls_DoNotCorruptInternalDictionary()
        {
            const int threadCount = 30;
            var workspaceRoot = new DirectoryPath("/c/workspace/");
            var filePath = new FilePath("/c/workspace/agent.mcs.yml");

            var clientInfo = new FakeClientInformation(workspaceRoot);
            // Slow factory widens the race window so multiple threads enter the
            // check-then-set block before any single thread finishes writing.
            var factory = new SlowFileProviderFactory(delayMs: 15);
            var provider = new ClientWorkspaceFileProvider(clientInfo, factory);

            using var barrier = new Barrier(threadCount);
            var exceptions = new ConcurrentBag<Exception>();

            var threads = Enumerable.Range(0, threadCount).Select(_ => new Thread(() =>
            {
                barrier.SignalAndWait();
                try
                {
                    provider.GetFileInfo(filePath);
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            })).ToList();

            threads.ForEach(t => t.Start());
            threads.ForEach(t => t.Join(timeout: TimeSpan.FromSeconds(10)));

            Assert.Empty(exceptions);
        }

        [Fact]
        public void GetFileInfo_SequentialCalls_ReturnValidFileInfo()
        {
            var workspaceRoot = new DirectoryPath("/c/workspace/");
            var filePath = new FilePath("/c/workspace/agent.mcs.yml");

            var clientInfo = new FakeClientInformation(workspaceRoot);
            var factory = new SlowFileProviderFactory(delayMs: 0);
            var provider = new ClientWorkspaceFileProvider(clientInfo, factory);

            var result = provider.GetFileInfo(filePath);

            Assert.NotNull(result);
        }

        [Fact]
        public void GetFileInfo_PathOutsideWorkspace_ThrowsInvalidOperationException()
        {
            var workspaceRoot = new DirectoryPath("/c/workspace/");
            var outsidePath = new FilePath("/c/other/agent.mcs.yml");

            var clientInfo = new FakeClientInformation(workspaceRoot);
            var factory = new SlowFileProviderFactory(delayMs: 0);
            var provider = new ClientWorkspaceFileProvider(clientInfo, factory);

            Assert.Throws<InvalidOperationException>(() => provider.GetFileInfo(outsidePath));
        }

        // ── Test doubles ──────────────────────────────────────────────────────────

        private class FakeClientInformation : IClientInformation
        {
            private readonly DirectoryPath _workspaceRoot;

            public FakeClientInformation(DirectoryPath workspaceRoot)
            {
                _workspaceRoot = workspaceRoot;
            }

            public CultureInfo CultureInfo => CultureInfo.InvariantCulture;
            public InitializeParams InitializeParams => throw new NotSupportedException();

            public bool TryGetWorkspaceFolder(DirectoryPath directoryPath, [MaybeNullWhen(false)] out DirectoryPath clientWorkspaceFolder)
            {
                if (directoryPath.ToString().StartsWith(_workspaceRoot.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    clientWorkspaceFolder = _workspaceRoot;
                    return true;
                }

                clientWorkspaceFolder = default;
                return false;
            }
        }

        private class SlowFileProviderFactory : IFileProviderFactory
        {
            private readonly int _delayMs;

            public SlowFileProviderFactory(int delayMs)
            {
                _delayMs = delayMs;
            }

            public IFileProvider Create(string root)
            {
                if (_delayMs > 0)
                    Thread.Sleep(_delayMs);
                return new StubFileProvider();
            }

            private class StubFileProvider : IFileProvider
            {
                public IDirectoryContents GetDirectoryContents(string subpath) =>
                    new NotFoundDirectoryContents();

                public IFileInfo GetFileInfo(string subpath) =>
                    new NotFoundFileInfo(subpath);

                public IChangeToken Watch(string filter) =>
                    NullChangeToken.Singleton;

                private class NotFoundFileInfo : IFileInfo
                {
                    public NotFoundFileInfo(string name) { Name = name; }
                    public bool Exists => false;
                    public long Length => -1;
                    public string? PhysicalPath => null;
                    public string Name { get; }
                    public DateTimeOffset LastModified => DateTimeOffset.MinValue;
                    public bool IsDirectory => false;
                    public Stream CreateReadStream() => throw new FileNotFoundException(Name);
                }
            }
        }
    }
}
