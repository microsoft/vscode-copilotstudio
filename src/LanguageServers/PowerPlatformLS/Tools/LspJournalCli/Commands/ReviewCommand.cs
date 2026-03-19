namespace Microsoft.PowerPlatformLS.Tools.LspJournalCli.Commands
{
    using System.Text.Json;
    using Microsoft.PowerPlatformLS.Tools.LspJournalCli.Models;
    using Microsoft.PowerPlatformLS.Tools.LspJournalCli.Transport;

    /// <summary>
    /// Scans all journals and reports review annotation status.
    /// Supports summary view (all journals), detail view (single journal),
    /// and --suspects-only filtering.
    /// </summary>
    public static class ReviewCommand
    {
        public static async Task<int> RunAsync(string? name, bool suspectsOnly, CancellationToken cancellationToken = default)
        {
            var testAssets = ServerLocator.GetTestAssetsDir();
            var journalDir = Path.Combine(testAssets, "journals");

            if (!Directory.Exists(journalDir))
            {
                Console.Error.WriteLine($"Journals directory not found: {journalDir}");
                return 1;
            }

            if (name is not null)
            {
                var journalPath = Path.Combine(journalDir, $"{name}.journal.json");
                if (!File.Exists(journalPath))
                {
                    Console.Error.WriteLine($"Journal not found: {journalPath}");
                    return 1;
                }

                return await PrintDetailAsync(journalPath, name, suspectsOnly, cancellationToken);
            }

            // Summary across all journals
            var journals = Directory.GetFiles(journalDir, "*.journal.json")
                .OrderBy(f => f)
                .ToArray();

            if (journals.Length == 0)
            {
                Console.Error.WriteLine("No journals found.");
                return 1;
            }

            var totalConfirmed = 0;
            var totalSuspect = 0;
            var totalStale = 0;
            var totalUnreviewed = 0;

            foreach (var journalPath in journals)
            {
                var journalName = Path.GetFileNameWithoutExtension(journalPath).Replace(".journal", "");
                var journal = await LoadJournalAsync(journalPath, cancellationToken);
                if (journal is null) continue;

                var (confirmed, suspect, stale, unreviewed) = CountReviewStates(journal.Steps);
                totalConfirmed += confirmed;
                totalSuspect += suspect;
                totalStale += stale;
                totalUnreviewed += unreviewed;

                var total = journal.Steps.Count;
                var marker = suspect > 0 || stale > 0 ? " ⚠" : "";

                if (suspectsOnly && suspect == 0 && stale == 0)
                    continue;

                Console.Write($"  {journalName,-40} {total,3} steps: ");
                WriteColoredCount("confirmed", confirmed, ConsoleColor.Green);
                Console.Write(", ");
                WriteColoredCount("suspect", suspect, ConsoleColor.Red);
                Console.Write(", ");
                WriteColoredCount("stale", stale, ConsoleColor.Yellow);
                Console.Write($", {unreviewed} unreviewed");
                Console.WriteLine(marker);
            }

            Console.WriteLine();
            Console.WriteLine($"  Totals: {totalConfirmed} confirmed, {totalSuspect} suspect, {totalStale} stale, {totalUnreviewed} unreviewed");

            if (totalSuspect > 0 || totalStale > 0)
            {
                Console.WriteLine($"\n  Run: dotnet run -- review <name> to see details.");
            }

            return 0;
        }

        private static async Task<int> PrintDetailAsync(string journalPath, string name, bool suspectsOnly, CancellationToken cancellationToken)
        {
            var journal = await LoadJournalAsync(journalPath, cancellationToken);
            if (journal is null) return 1;

            var (confirmed, suspect, stale, unreviewed) = CountReviewStates(journal.Steps);
            var total = journal.Steps.Count;

            Console.WriteLine($"Journal: {name} ({total} steps)");
            Console.Write($"  Reviewed: ");
            WriteColoredCount("confirmed", confirmed, ConsoleColor.Green);
            Console.Write(", ");
            WriteColoredCount("suspect", suspect, ConsoleColor.Red);
            Console.Write(", ");
            WriteColoredCount("stale", stale, ConsoleColor.Yellow);
            Console.Write($", {unreviewed} unreviewed");
            Console.WriteLine();

            // Print stale steps
            var staleSteps = GetAnnotatedSteps(journal.Steps, "stale");
            if (staleSteps.Count > 0)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"\n  ⚠ Stale (baseline changed since review):");
                Console.ResetColor();
                foreach (var (index, step, fingerprint) in staleSteps)
                {
                    var location = FormatLocation(fingerprint);
                    var suspectTag = step.SuspectId is not null ? $" [{step.SuspectId}]" : "";
                    Console.WriteLine($"    step {index + 1,-3} {step.Step} {location}{suspectTag}");
                    if (step.ReviewNote is not null)
                        Console.WriteLine($"          {step.ReviewNote}");
                }
            }

            // Print suspect steps
            var suspectSteps = GetAnnotatedSteps(journal.Steps, "suspect");
            if (suspectSteps.Count > 0)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\n  Suspects:");
                Console.ResetColor();
                foreach (var (index, step, fingerprint) in suspectSteps)
                {
                    var location = FormatLocation(fingerprint);
                    var suspectTag = step.SuspectId is not null ? $" [{step.SuspectId}]" : "";
                    Console.WriteLine($"    step {index + 1,-3} {step.Step} {location}{suspectTag}");
                    if (step.ReviewNote is not null)
                        Console.WriteLine($"          {step.ReviewNote}");
                }
            }

            // In detail mode, also show unreviewed unless --suspects-only
            if (!suspectsOnly)
            {
                var unreviewedSteps = GetAnnotatedSteps(journal.Steps, null);
                if (unreviewedSteps.Count > 0)
                {
                    Console.WriteLine($"\n  Unreviewed ({unreviewedSteps.Count}):");
                    foreach (var (index, step, fingerprint) in unreviewedSteps)
                    {
                        var location = FormatLocation(fingerprint);
                        Console.ForegroundColor = ConsoleColor.DarkGray;
                        Console.WriteLine($"    step {index + 1,-3} {step.Step} {location}");
                        Console.ResetColor();
                    }
                }
            }

            return 0;
        }

        private static async Task<Journal?> LoadJournalAsync(string path, CancellationToken cancellationToken)
        {
            try
            {
                var json = await File.ReadAllTextAsync(path, cancellationToken);
                return JsonSerializer.Deserialize<Journal>(json, SerializationOptions.Default);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to load {path}: {ex.Message}");
                return null;
            }
        }

        private static (int Confirmed, int Suspect, int Stale, int Unreviewed) CountReviewStates(List<JournalStep> steps)
        {
            int confirmed = 0, suspect = 0, stale = 0, unreviewed = 0;
            foreach (var step in steps)
            {
                switch (step.Review)
                {
                    case "confirmed": confirmed++; break;
                    case "suspect": suspect++; break;
                    case "stale": stale++; break;
                    default: unreviewed++; break;
                }
            }

            return (confirmed, suspect, stale, unreviewed);
        }

        private static List<(int Index, JournalStep Step, StepFingerprint Fingerprint)> GetAnnotatedSteps(
            List<JournalStep> steps, string? reviewState)
        {
            var fingerprints = StepFingerprint.BuildFingerprints(steps);
            var result = new List<(int, JournalStep, StepFingerprint)>();

            for (var i = 0; i < steps.Count; i++)
            {
                if (steps[i].Review == reviewState)
                {
                    result.Add((i, steps[i], fingerprints[i]));
                }
            }

            return result;
        }

        private static string FormatLocation(StepFingerprint fp)
        {
            if (fp.Uri is null) return "";
            var uri = fp.Uri;
            // Shorten ${workspace}/ prefix for display
            if (uri.Contains("${workspace}/"))
                uri = uri[(uri.IndexOf("${workspace}/") + "${workspace}/".Length)..];
            if (fp.Line.HasValue)
                return $"{uri}:{fp.Line}:{fp.Character}";
            return uri;
        }

        private static void WriteColoredCount(string label, int count, ConsoleColor color)
        {
            if (count > 0)
            {
                Console.ForegroundColor = color;
                Console.Write($"{count} {label}");
                Console.ResetColor();
            }
            else
            {
                Console.Write($"{count} {label}");
            }
        }
    }
}