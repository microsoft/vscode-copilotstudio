namespace Microsoft.PowerPlatformLS.Tools.LspJournalCli.Transport
{
    using System.Reflection;

    /// <summary>
    /// Locates the LSP server binary. The server is at a known relative position
    /// in the build output tree.
    /// </summary>
    public static class ServerLocator
    {
        /// <summary>
        /// Relative path from the LspJournalCli project directory to the LanguageServerHost project directory.
        /// </summary>
        private const string ServerProjectRelativePath = @"..\..\LanguageServerHost";

        private static string ServerBinaryName => OperatingSystem.IsWindows()
            ? "LanguageServerHost.exe"
            : "LanguageServerHost";

        /// <summary>
        /// Find the LanguageServerHost.exe binary, searching the server project's bin directory.
        /// </summary>
        public static string FindServer()
        {
            // Strategy: walk from this assembly's location up to the Tools/LspJournalCli dir,
            // then resolve the server project relative to that.
            var cliProjectDir = FindCliProjectDir()
                ?? throw new InvalidOperationException(
                    "Cannot locate the LspJournalCli project directory. " +
                    "Ensure the tool is run via 'dotnet run --project ...' or from the build output.");

            var serverProjectDir = Path.GetFullPath(Path.Combine(cliProjectDir, ServerProjectRelativePath));
            if (!Directory.Exists(serverProjectDir))
            {
                throw new InvalidOperationException(
                    $"Server project directory not found: {serverProjectDir}");
            }

            // Search bin/ for the server binary (handles Debug/Release and TFM variations)
            var binDir = Path.Combine(serverProjectDir, "bin");
            if (Directory.Exists(binDir))
            {
                var candidates = Directory.GetFiles(binDir, ServerBinaryName, SearchOption.AllDirectories);
                if (candidates.Length > 0)
                {
                    // Prefer the most recently written binary
                    return candidates
                        .OrderByDescending(File.GetLastWriteTimeUtc)
                        .First();
                }
            }

            throw new InvalidOperationException(
                $"LanguageServerHost.exe not found under {binDir}. " +
                "Build the server first: dotnet build src/vscode/LanguageServers/PowerPlatformLS/LanguageServerHost/LanguageServerHost.csproj");
        }

        /// <summary>
        /// Resolve the TestAssets directory relative to the CLI project.
        /// </summary>
        public static string GetTestAssetsDir()
        {
            var cliProjectDir = FindCliProjectDir()
                ?? throw new InvalidOperationException("Cannot locate the LspJournalCli project directory.");

            return Path.Combine(cliProjectDir, "TestAssets");
        }

        /// <summary>
        /// Walk up from the executing assembly to find the LspJournalCli project directory
        /// (identified by the presence of LspJournalCli.csproj).
        /// </summary>
        private static string? FindCliProjectDir()
        {
            // Start from the assembly location
            var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

            // Walk up looking for the csproj (handles bin/Debug/net10.0/ nesting)
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir, "LspJournalCli.csproj")))
                {
                    return dir;
                }

                dir = Path.GetDirectoryName(dir);
            }

            // Fallback: try working directory
            var cwd = Directory.GetCurrentDirectory();
            while (cwd is not null)
            {
                if (File.Exists(Path.Combine(cwd, "LspJournalCli.csproj")))
                {
                    return cwd;
                }

                cwd = Path.GetDirectoryName(cwd);
            }

            return null;
        }
    }
}