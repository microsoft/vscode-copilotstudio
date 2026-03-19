namespace Microsoft.PowerPlatformLS.Tools.LspJournalCli
{
    using System.CommandLine;
    using Microsoft.PowerPlatformLS.Tools.LspJournalCli.Commands;

    /// <summary>
    /// LSP Journal CLI — run journals, validate inline, produce self-validating output.
    ///
    /// Usage:
    ///   lspjournal &lt;name&gt;         Run a single journal by name
    ///   lspjournal --all           Run all journals
    ///   lspjournal accept &lt;name&gt;   Accept pending changes for a journal
    ///   lspjournal accept --all    Accept all pending changes
    ///   lspjournal discard &lt;name&gt;  Discard pending changes for a journal
    ///   lspjournal discard --all   Discard all pending changes
    ///   lspjournal pending         List pending changes
    ///   lspjournal review           Summarize review status across all journals
    ///   lspjournal review &lt;name&gt;    Detail review status for one journal
    ///   lspjournal diff &lt;a&gt; &lt;b&gt;   Compare two journal files
    /// </summary>
    public static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            var rootCommand = new RootCommand("LSP Journal CLI — run, validate, and record LSP behavior.");

            // Default command: run a journal by name
            var nameArgument = new Argument<string?>(
                "name",
                () => null,
                "Journal name to run (e.g. 'lifecycle'). Resolves to TestAssets/journals/<name>.journal.json.");

            var allOption = new Option<bool>(
                "--all",
                "Run all journals in the TestAssets/journals directory.");

            var verboseOption = new Option<bool>(
                "--verbose",
                "Enable wire-level protocol tracing to stderr.");

            var forceOption = new Option<bool>(
                "--force",
                "Force write to .pending/ even when behavior is unchanged (e.g. to update metadata schema).");


            rootCommand.AddArgument(nameArgument);
            rootCommand.AddOption(allOption);
            rootCommand.AddOption(verboseOption);
            rootCommand.AddOption(forceOption);

            rootCommand.SetHandler(async (string? name, bool all, bool verbose, bool force) =>
            {
                if (all)
                {
                    Environment.ExitCode = await RunCommand.RunAllAsync(verbose: verbose, force: force);
                }
                else if (name is not null)
                {
                    Environment.ExitCode = await RunCommand.RunByNameAsync(name, verbose: verbose, force: force);
                }
                else
                {
                    Console.Error.WriteLine("Provide a journal name or --all. Run with --help for usage.");
                    Environment.ExitCode = 1;
                }
            }, nameArgument, allOption, verboseOption, forceOption);

            rootCommand.AddCommand(BuildDiffCommand());
            rootCommand.AddCommand(BuildAcceptCommand());
            rootCommand.AddCommand(BuildDiscardCommand());
            rootCommand.AddCommand(BuildPendingCommand());
            rootCommand.AddCommand(BuildReviewCommand());

            return await rootCommand.InvokeAsync(args);
        }

        private static Command BuildDiffCommand()
        {
            var command = new Command("diff", "Compare two journal files and report structured differences.");

            var journalAOption = new Option<FileInfo>(
                "--journal-a",
                "Path to the first journal file.")
            { IsRequired = true };

            var journalBOption = new Option<FileInfo>(
                "--journal-b",
                "Path to the second journal file.")
            { IsRequired = true };

            command.AddOption(journalAOption);
            command.AddOption(journalBOption);

            command.SetHandler(async (FileInfo journalA, FileInfo journalB) =>
            {
                Environment.ExitCode = await DiffCommand.RunAsync(journalA, journalB);
            }, journalAOption, journalBOption);

            return command;
        }

        private static Command BuildAcceptCommand()
        {
            var command = new Command("accept", "Accept pending journal changes (promote .pending/ → baseline).");

            var nameArgument = new Argument<string?>(
                "name",
                () => null,
                "Journal name to accept (e.g. 'lifecycle').");

            var allOption = new Option<bool>(
                "--all",
                "Accept all pending journals.");

            command.AddArgument(nameArgument);
            command.AddOption(allOption);

            command.SetHandler(async (string? name, bool all) =>
            {
                if (all)
                {
                    Environment.ExitCode = await AcceptCommand.AcceptAllAsync();
                }
                else if (name is not null)
                {
                    Environment.ExitCode = await AcceptCommand.AcceptByNameAsync(name);
                }
                else
                {
                    Console.Error.WriteLine("Provide a journal name or --all. Run with --help for usage.");
                    Environment.ExitCode = 1;
                }
            }, nameArgument, allOption);

            return command;
        }

        private static Command BuildDiscardCommand()
        {
            var command = new Command("discard", "Discard pending journal changes (delete .pending/ files).");

            var nameArgument = new Argument<string?>(
                "name",
                () => null,
                "Journal name to discard (e.g. 'lifecycle').");

            var allOption = new Option<bool>(
                "--all",
                "Discard all pending journals.");

            command.AddArgument(nameArgument);
            command.AddOption(allOption);

            command.SetHandler(async (string? name, bool all) =>
            {
                if (all)
                {
                    Environment.ExitCode = await AcceptCommand.DiscardAllAsync();
                }
                else if (name is not null)
                {
                    Environment.ExitCode = await AcceptCommand.DiscardByNameAsync(name);
                }
                else
                {
                    Console.Error.WriteLine("Provide a journal name or --all. Run with --help for usage.");
                    Environment.ExitCode = 1;
                }
            }, nameArgument, allOption);

            return command;
        }

        private static Command BuildPendingCommand()
        {
            var command = new Command("pending", "List pending journal changes.");

            command.SetHandler(() =>
            {
                Environment.ExitCode = AcceptCommand.ListPending();
            });

            return command;
        }

        private static Command BuildReviewCommand()
        {
            var command = new Command("review", "Scan journals and report review annotation status.");

            var nameArgument = new Argument<string?>(
                "name",
                () => null,
                "Journal name to show details for (e.g. 'lifecycle').");

            var suspectsOnlyOption = new Option<bool>(
                "--suspects-only",
                "Only show steps marked 'suspect' or 'stale'.");

            command.AddArgument(nameArgument);
            command.AddOption(suspectsOnlyOption);

            command.SetHandler(async (string? name, bool suspectsOnly) =>
            {
                Environment.ExitCode = await ReviewCommand.RunAsync(name, suspectsOnly);
            }, nameArgument, suspectsOnlyOption);

            return command;
        }
    }
}