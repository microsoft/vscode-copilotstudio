namespace Microsoft.PowerPlatformLS.Tools.LspJournalCli.Execution
{
    using System.Diagnostics;

    /// <summary>
    /// Collects git branch/commit metadata for journal entries.
    /// </summary>
    public static class GitMetadata
    {
        public static string? GetBranch()
        {
            return RunGit("rev-parse --abbrev-ref HEAD");
        }

        public static string? GetCommit()
        {
            return RunGit("rev-parse --short HEAD");
        }

        /// <summary>
        /// Get the merge-base commit between HEAD and the given base branch.
        /// Returns the abbreviated hash of the branch point.
        /// </summary>
        public static string? GetBranchBase(string baseBranch = "main")
        {
            var mergeBase = RunGit($"merge-base HEAD {baseBranch}");
            if (mergeBase is null) return null;

            // Return abbreviated hash for readability
            return RunGit($"rev-parse --short {mergeBase}") ?? mergeBase;
        }

        /// <summary>
        /// Get the number of commits between the merge-base (with the given base branch) and HEAD.
        /// </summary>
        public static int? GetBranchDepth(string baseBranch = "main")
        {
            var mergeBase = RunGit($"merge-base HEAD {baseBranch}");
            if (mergeBase is null) return null;

            var count = RunGit($"rev-list --count {mergeBase}..HEAD");
            return int.TryParse(count, out var n) ? n : null;
        }

        private static string? RunGit(string arguments)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                using var process = Process.Start(psi);
                if (process is null)
                {
                    return null;
                }

                var output = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit(5000);
                return process.ExitCode == 0 ? output : null;
            }
            catch
            {
                return null;
            }
        }
    }
}