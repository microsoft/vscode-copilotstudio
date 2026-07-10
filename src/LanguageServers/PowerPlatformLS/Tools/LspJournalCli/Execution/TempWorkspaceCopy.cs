namespace Microsoft.PowerPlatformLS.Tools.LspJournalCli.Execution
{
    using System;
    using System.IO;

    /// <summary>
    /// An isolated, disposable copy of a fixture workspace.
    /// <para>
    /// The language server rewrites workspace files while indexing them on load — for
    /// example, migrating legacy <c># Name:</c> comment headers into structured
    /// <c>mcs.metadata:</c> blocks. Running a journal directly against the committed
    /// fixtures therefore mutates them (dirtying the working tree) and, during
    /// <c>--all</c>, lets one journal's rewrites break a later journal's text-hash check.
    /// </para>
    /// <para>
    /// Journals run the server against one of these throwaway copies instead. The copy is
    /// deleted on <see cref="Dispose"/>, so committed fixtures stay pristine and journals
    /// never contaminate one another. Text-hash validation stays bound to the committed
    /// fixture (see <see cref="DocumentTextPolicy"/>) so recorded hashes remain stable.
    /// </para>
    /// </summary>
    public sealed class TempWorkspaceCopy : IDisposable
    {
        private TempWorkspaceCopy(string workspacePath)
        {
            WorkspacePath = workspacePath;
        }

        /// <summary>Absolute path to the throwaway workspace copy.</summary>
        public string WorkspacePath { get; }

        /// <summary>
        /// The <c>file://</c> URI of the copy, without a trailing slash — matching the
        /// shape the runner expands <c>${workspace}</c> placeholders into.
        /// </summary>
        public string Uri => new Uri(WorkspacePath).ToString().TrimEnd('/');

        /// <summary>
        /// Recursively copy <paramref name="sourceWorkspacePath"/> into a fresh temp directory.
        /// </summary>
        public static TempWorkspaceCopy Create(string sourceWorkspacePath)
        {
            if (string.IsNullOrEmpty(sourceWorkspacePath))
            {
                throw new ArgumentException("Source workspace path must be provided.", nameof(sourceWorkspacePath));
            }

            var source = Path.GetFullPath(sourceWorkspacePath);
            if (!Directory.Exists(source))
            {
                throw new DirectoryNotFoundException($"Workspace fixture not found: {source}");
            }

            var destination = Path.Combine(Path.GetTempPath(), "lsp-journal-" + Guid.NewGuid().ToString("N"));
            CopyDirectory(source, destination);
            return new TempWorkspaceCopy(destination);
        }

        private static void CopyDirectory(string sourceDir, string destinationDir)
        {
            Directory.CreateDirectory(destinationDir);

            foreach (var file in Directory.EnumerateFiles(sourceDir))
            {
                var destFile = Path.Combine(destinationDir, Path.GetFileName(file));
                File.Copy(file, destFile, overwrite: true);
            }

            foreach (var directory in Directory.EnumerateDirectories(sourceDir))
            {
                var destSubDir = Path.Combine(destinationDir, Path.GetFileName(directory));
                CopyDirectory(directory, destSubDir);
            }
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(WorkspacePath))
                {
                    Directory.Delete(WorkspacePath, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup: a leftover temp directory must never fail a journal run.
            }
        }
    }
}
