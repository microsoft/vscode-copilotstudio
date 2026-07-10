namespace Microsoft.PowerPlatformLS.Tools.LspJournalCli.UnitTests
{
    using System;
    using System.IO;
    using System.Text;
    using Microsoft.PowerPlatformLS.Tools.LspJournalCli.Execution;
    using Xunit;

    /// <summary>
    /// Guards the isolation invariant behind issue #313: the language server rewrites
    /// workspace files on load (migrating legacy "# Name:" headers into "mcs.metadata:"
    /// blocks), so journals must run against a throwaway copy. If the server ever writes
    /// through to the committed fixture again, these tests fail.
    /// </summary>
    public sealed class TempWorkspaceCopyTests
    {
        private static string CreateSourceWorkspace()
        {
            var root = Path.Combine(Path.GetTempPath(), "lsp-journal-src-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            // Use LF-only content to mirror the committed fixtures.
            File.WriteAllText(Path.Combine(root, "agent.mcs.yml"), "kind: GptComponentMetadata\n", new UTF8Encoding(false));
            var topics = Path.Combine(root, "topics");
            Directory.CreateDirectory(topics);
            File.WriteAllText(Path.Combine(topics, "Greeting.mcs.yml"), "# Name: Greeting\nkind: AdaptiveDialog\n", new UTF8Encoding(false));
            return root;
        }

        [Fact]
        public void Create_CopiesAllFilesAndSubdirectoriesWithIdenticalBytes()
        {
            var source = CreateSourceWorkspace();
            try
            {
                using var copy = TempWorkspaceCopy.Create(source);

                var copiedAgent = Path.Combine(copy.WorkspacePath, "agent.mcs.yml");
                var copiedGreeting = Path.Combine(copy.WorkspacePath, "topics", "Greeting.mcs.yml");

                Assert.True(File.Exists(copiedAgent));
                Assert.True(File.Exists(copiedGreeting));
                Assert.Equal(
                    File.ReadAllBytes(Path.Combine(source, "agent.mcs.yml")),
                    File.ReadAllBytes(copiedAgent));
                Assert.Equal(
                    File.ReadAllBytes(Path.Combine(source, "topics", "Greeting.mcs.yml")),
                    File.ReadAllBytes(copiedGreeting));
            }
            finally
            {
                Directory.Delete(source, recursive: true);
            }
        }

        [Fact]
        public void Create_PointsAtCopyNotSource()
        {
            var source = CreateSourceWorkspace();
            try
            {
                using var copy = TempWorkspaceCopy.Create(source);

                Assert.NotEqual(
                    Path.GetFullPath(source),
                    Path.GetFullPath(copy.WorkspacePath));
                Assert.Equal(
                    Path.GetFullPath(copy.WorkspacePath),
                    Path.GetFullPath(new Uri(copy.Uri).LocalPath));
            }
            finally
            {
                Directory.Delete(source, recursive: true);
            }
        }

        [Fact]
        public void MutatingCopy_DoesNotAffectSource()
        {
            // This is the exact scenario that mutated committed fixtures: the server rewrites
            // a file (changed content) and deletes another during workspace indexing. Those
            // writes must land on the copy only.
            var source = CreateSourceWorkspace();
            var sourceGreeting = Path.Combine(source, "topics", "Greeting.mcs.yml");
            var originalGreetingBytes = File.ReadAllBytes(sourceGreeting);
            try
            {
                using var copy = TempWorkspaceCopy.Create(source);

                var copiedGreeting = Path.Combine(copy.WorkspacePath, "topics", "Greeting.mcs.yml");
                File.WriteAllText(copiedGreeting, "mcs.metadata:\r\n  componentName: Greeting\r\nkind: AdaptiveDialog", new UTF8Encoding(false));
                File.Delete(Path.Combine(copy.WorkspacePath, "agent.mcs.yml"));

                Assert.Equal(originalGreetingBytes, File.ReadAllBytes(sourceGreeting));
                Assert.True(File.Exists(Path.Combine(source, "agent.mcs.yml")));
            }
            finally
            {
                Directory.Delete(source, recursive: true);
            }
        }

        [Fact]
        public void Dispose_RemovesCopyDirectory()
        {
            var source = CreateSourceWorkspace();
            try
            {
                string copyPath;
                using (var copy = TempWorkspaceCopy.Create(source))
                {
                    copyPath = copy.WorkspacePath;
                    Assert.True(Directory.Exists(copyPath));
                }

                Assert.False(Directory.Exists(copyPath));
            }
            finally
            {
                Directory.Delete(source, recursive: true);
            }
        }
    }
}
