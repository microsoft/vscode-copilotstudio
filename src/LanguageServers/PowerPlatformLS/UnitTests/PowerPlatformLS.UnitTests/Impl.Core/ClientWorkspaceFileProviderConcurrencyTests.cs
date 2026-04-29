namespace Microsoft.PowerPlatformLS.UnitTests.Impl.Core
{
    using System;
    using System.Collections.Concurrent;
    using System.Globalization;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.Extensions.FileProviders;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models.Lsp;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Impl.Core.IO;
    using Xunit;
    using Xunit.Abstractions;

    public class ClientWorkspaceFileProviderConcurrencyTests
    {
        private readonly ITestOutputHelper _out;

        public ClientWorkspaceFileProviderConcurrencyTests(ITestOutputHelper output)
        {
            _out = output;
        }

        // Reproduces microsoft/vscode-copilotstudio#156. ClientWorkspaceFileProvider is
        // registered as a singleton (CoreLspModule.cs) and mutates a non-concurrent
        // Dictionary<DirectoryPath, IFileProvider> from concurrent LSP request handlers.
        // .NET detects this and throws
        //   "Operations that change non-concurrent collections must have exclusive access."
        // The Barrier inside BarrierGatedFileProviderFactory parks every thread inside the
        // `if (!_fileProviders.ContainsKey(...))` branch of GetFileProvider so all threads
        // proceed to the racey `_fileProviders[key] = ...` indexer-set near-simultaneously,
        // which is sufficient to trip the runtime's concurrent-mutation guard.
        [Fact]
        public async Task GetFileInfo_ManyThreadsDifferentWorkspaceFolders_DoesNotCorruptDictionary()
        {
            const int N = 64;

            var filePaths = new FilePath[N];
            for (int i = 0; i < N; i++)
            {
                filePaths[i] = new FilePath($"/ws{i}/file{i}.yml");
            }

            var clientInfo = new IdentityClientInformation();

            using var barrier = new Barrier(N);
            var factory = new BarrierGatedFileProviderFactory(barrier);

            var sut = new ClientWorkspaceFileProvider(clientInfo, factory);

            var caught = new ConcurrentBag<Exception>();
            var tasks = new Task[N];
            for (int i = 0; i < N; i++)
            {
                var p = filePaths[i];
                tasks[i] = Task.Run(() =>
                {
                    try
                    {
                        sut.GetFileInfo(p);
                    }
                    catch (Exception ex)
                    {
                        caught.Add(ex);
                    }
                });
            }

            await Task.WhenAll(tasks);

            // Group every captured exception by (type, top-line message) so we can see
            // whether the user-reported "Operations that change non-concurrent collections
            // must have exclusive access." InvalidOperationException fires alongside the
            // KeyNotFoundException / NullReferenceException variants.
            var summary = new StringBuilder();
            summary.AppendLine($"Total tasks launched: {N}; tasks that threw: {caught.Count}.");

            var groups = caught
                .GroupBy(e => (e.GetType().FullName ?? e.GetType().Name, FirstLineOf(e.Message)))
                .OrderByDescending(g => g.Count())
                .ToArray();

            for (int i = 0; i < groups.Length; i++)
            {
                var g = groups[i];
                summary.AppendLine($"  [{i + 1}] {g.Count()}x {g.Key.Item1}: {g.Key.Item2}");
            }

            // Surface a representative full exception (with stack) per distinct group.
            for (int i = 0; i < groups.Length; i++)
            {
                summary.AppendLine();
                summary.AppendLine($"--- Representative for group [{i + 1}] ---");
                summary.AppendLine(groups[i].First().ToString());
            }

            _out.WriteLine(summary.ToString());

            Assert.True(caught.IsEmpty, summary.ToString());
        }

        private static string FirstLineOf(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return string.Empty;
            }

            var nl = s.IndexOf('\n');
            return nl < 0 ? s : s.Substring(0, nl).TrimEnd('\r');
        }

        private sealed class BarrierGatedFileProviderFactory : IFileProviderFactory
        {
            private readonly Barrier _barrier;

            public BarrierGatedFileProviderFactory(Barrier barrier)
            {
                _barrier = barrier;
            }

            public IFileProvider Create(string root)
            {
                _barrier.SignalAndWait();
                return new NullFileProvider();
            }
        }

        // Maps every queried directory to itself as the workspace folder, so each thread's
        // distinct ParentDirectoryPath becomes a distinct dictionary key — maximising the
        // racey insertion path inside GetFileProvider.
        private sealed class IdentityClientInformation : IClientInformation
        {
            public CultureInfo CultureInfo => CultureInfo.InvariantCulture;

            public InitializeParams InitializeParams => throw new NotImplementedException("Probe does not exercise InitializeParams.");

            public bool TryGetWorkspaceFolder(DirectoryPath directoryPath, out DirectoryPath clientWorkspaceFolder)
            {
                clientWorkspaceFolder = directoryPath;
                return true;
            }
        }
    }
}
