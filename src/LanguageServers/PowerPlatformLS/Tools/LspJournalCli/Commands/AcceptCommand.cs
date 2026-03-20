namespace Microsoft.PowerPlatformLS.Tools.LspJournalCli.Commands
{
    using System.Text.Json;
    using Microsoft.PowerPlatformLS.Tools.LspJournalCli.Execution;
    using Microsoft.PowerPlatformLS.Tools.LspJournalCli.Models;
    using Microsoft.PowerPlatformLS.Tools.LspJournalCli.Transport;

    /// <summary>
    /// Promotes pending journal changes to the committed baseline.
    /// Pending files live in .pending/ next to the journal directory.
    /// </summary>
    public static class AcceptCommand
    {
        /// <summary>
        /// Accept pending changes for a single journal by name.
        /// </summary>
        public static async Task<int> AcceptByNameAsync(string name, CancellationToken cancellationToken = default)
        {
            var journalsDir = GetJournalsDir();
            var pendingDir = GetPendingDir(journalsDir);
            var safeName = Path.GetFileName(name);
            if (string.IsNullOrWhiteSpace(safeName) || !string.Equals(safeName, name, StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"Invalid journal name: '{name}'.");
                return 1;
            }

            var pendingPath = Path.Combine(pendingDir, $"{safeName}.journal.json");

            if (!File.Exists(pendingPath))
            {
                Console.Error.WriteLine($"No pending changes for '{safeName}'.");
                Console.Error.WriteLine($"  Expected: {pendingPath}");
                return 1;
            }

            var baselinePath = Path.Combine(journalsDir, $"{safeName}.journal.json");
            await MergeAnnotationsAsync(baselinePath, pendingPath, cancellationToken);
            File.Delete(pendingPath);

            Console.WriteLine($"Accepted: {name}");
            Console.WriteLine($"  {pendingPath} → {baselinePath}");

            CleanupPendingDir(pendingDir);
            return 0;
        }

        /// <summary>
        /// Accept pending changes for all journals.
        /// </summary>
        public static async Task<int> AcceptAllAsync(CancellationToken cancellationToken = default)
        {
            var journalsDir = GetJournalsDir();
            var pendingDir = GetPendingDir(journalsDir);

            if (!Directory.Exists(pendingDir))
            {
                Console.WriteLine("No pending changes.");
                return 0;
            }

            var pendingFiles = Directory.GetFiles(pendingDir, "*.journal.json");
            if (pendingFiles.Length == 0)
            {
                Console.WriteLine("No pending changes.");
                return 0;
            }

            foreach (var pendingPath in pendingFiles.OrderBy(f => f))
            {
                var fileName = Path.GetFileName(pendingPath);
                var baselinePath = Path.Combine(journalsDir, fileName);
                await MergeAnnotationsAsync(baselinePath, pendingPath, cancellationToken);
                File.Delete(pendingPath);

                var name = Path.GetFileNameWithoutExtension(fileName).Replace(".journal", "");
                Console.WriteLine($"  Accepted: {name}");
            }

            Console.WriteLine($"\nAccepted {pendingFiles.Length} journal(s).");
            CleanupPendingDir(pendingDir);
            return 0;
        }

        /// <summary>
        /// Discard pending changes for a single journal by name.
        /// </summary>
        public static async Task<int> DiscardByNameAsync(string name, CancellationToken cancellationToken = default)
        {
            var journalsDir = GetJournalsDir();
            var pendingDir = GetPendingDir(journalsDir);
            var pendingPath = Path.Combine(pendingDir, $"{name}.journal.json");

            if (!File.Exists(pendingPath))
            {
                Console.Error.WriteLine($"No pending changes for '{name}'.");
                return 1;
            }

            File.Delete(pendingPath);
            Console.WriteLine($"Discarded: {name}");

            CleanupPendingDir(pendingDir);
            return 0;
        }

        /// <summary>
        /// Discard pending changes for all journals.
        /// </summary>
        public static async Task<int> DiscardAllAsync(CancellationToken cancellationToken = default)
        {
            var journalsDir = GetJournalsDir();
            var pendingDir = GetPendingDir(journalsDir);

            if (!Directory.Exists(pendingDir))
            {
                Console.WriteLine("No pending changes to discard.");
                return 0;
            }

            var pendingFiles = Directory.GetFiles(pendingDir, "*.journal.json");
            if (pendingFiles.Length == 0)
            {
                Console.WriteLine("No pending changes to discard.");
                return 0;
            }

            foreach (var pendingPath in pendingFiles)
            {
                File.Delete(pendingPath);
            }

            Console.WriteLine($"Discarded {pendingFiles.Length} pending journal(s).");
            CleanupPendingDir(pendingDir);
            return 0;
        }

        /// <summary>
        /// List pending changes.
        /// </summary>
        public static int ListPending()
        {
            var journalsDir = GetJournalsDir();
            var pendingDir = GetPendingDir(journalsDir);

            if (!Directory.Exists(pendingDir))
            {
                Console.WriteLine("No pending changes.");
                return 0;
            }

            var pendingFiles = Directory.GetFiles(pendingDir, "*.journal.json");
            if (pendingFiles.Length == 0)
            {
                Console.WriteLine("No pending changes.");
                return 0;
            }

            Console.WriteLine($"Pending changes ({pendingFiles.Length}):");
            foreach (var pendingPath in pendingFiles.OrderBy(f => f))
            {
                var name = Path.GetFileNameWithoutExtension(Path.GetFileName(pendingPath)).Replace(".journal", "");
                Console.WriteLine($"  {name}");
            }

            Console.WriteLine($"\n  dotnet run -- accept --all   to accept all");
            Console.WriteLine($"  dotnet run -- discard --all  to discard all");
            return 0;
        }

        /// <summary>
        /// Get the absolute path to the .pending/ directory for a given journals directory.
        /// </summary>
        public static string GetPendingDir(string journalsDir)
        {
            return Path.Combine(journalsDir, ".pending");
        }

        /// <summary>
        /// Get the path to a pending journal file.
        /// </summary>
        public static string GetPendingPath(string journalPath)
        {
            var journalsDir = Path.GetDirectoryName(journalPath) ?? Path.GetFullPath(".");
            var fileName = Path.GetFileName(journalPath);
            var pendingDir = GetPendingDir(journalsDir);
            return Path.Combine(pendingDir, fileName);
        }

        /// <summary>
        /// Ensure the .pending/ directory exists.
        /// </summary>
        public static void EnsurePendingDir(string journalsDir)
        {
            var pendingDir = GetPendingDir(journalsDir);
            Directory.CreateDirectory(pendingDir);
        }

        /// <summary>
        /// Merge annotations from the old baseline into the pending journal, then write
        /// the result as the new baseline. Uses fingerprint-based matching so annotations
        /// survive step reordering. When a step's expected value changed, its review
        /// becomes "stale" with an appended note.
        /// </summary>
        private static async Task MergeAnnotationsAsync(string baselinePath, string pendingPath, CancellationToken cancellationToken)
        {
            var pendingJson = await File.ReadAllTextAsync(pendingPath, cancellationToken);
            var pending = JsonSerializer.Deserialize<Journal>(pendingJson, SerializationOptions.Default)
                ?? throw new InvalidOperationException($"Failed to deserialize pending: {pendingPath}");

            if (File.Exists(baselinePath))
            {
                var baselineJson = await File.ReadAllTextAsync(baselinePath, cancellationToken);
                var baseline = JsonSerializer.Deserialize<Journal>(baselineJson, SerializationOptions.Default);

                if (baseline is { Steps.Count: > 0 })
                {
                    // Build annotation map from old baseline: fingerprint → (review, reviewNote, suspectId, expectedNorm)
                    var oldFingerprints = StepFingerprint.BuildFingerprints(baseline.Steps);
                    var annotationMap = new Dictionary<StepFingerprint, (string? Review, string? ReviewNote, string? SuspectId, string? ExpectedNorm)>();

                    for (var i = 0; i < baseline.Steps.Count; i++)
                    {
                        var step = baseline.Steps[i];
                        if (step.Review is null && step.ReviewNote is null && step.SuspectId is null)
                            continue; // No annotations to preserve

                        var expectedNorm = step.Expected.HasValue
                            ? OutputNormalizer.NormalizeToString(step.Expected.Value)
                            : null;

                        // First match wins — if duplicates exist, earlier one takes priority
                        annotationMap.TryAdd(oldFingerprints[i], (step.Review, step.ReviewNote, step.SuspectId, expectedNorm));
                    }

                    if (annotationMap.Count > 0)
                    {
                        // Apply annotations to pending steps
                        var newFingerprints = StepFingerprint.BuildFingerprints(pending.Steps);

                        for (var i = 0; i < pending.Steps.Count; i++)
                        {
                            if (annotationMap.TryGetValue(newFingerprints[i], out var ann))
                            {
                                var pendingExpected = pending.Steps[i].Expected;
                                var newExpectedNorm = pendingExpected.HasValue
                                    ? OutputNormalizer.NormalizeToString(pendingExpected.Value)
                                    : null;

                                if (ann.ExpectedNorm == newExpectedNorm)
                                {
                                    // Expected unchanged — carry annotations forward
                                    pending.Steps[i].Review = ann.Review;
                                    pending.Steps[i].ReviewNote = ann.ReviewNote;
                                    pending.Steps[i].SuspectId = ann.SuspectId;
                                }
                                else
                                {
                                    // Expected changed — mark stale, preserve note with flag
                                    pending.Steps[i].Review = "stale";
                                    pending.Steps[i].SuspectId = ann.SuspectId;
                                    var note = ann.ReviewNote ?? "";
                                    if (!note.Contains("[baseline changed"))
                                    {
                                        note = string.IsNullOrEmpty(note)
                                            ? "[baseline changed — re-review needed]"
                                            : $"{note} [baseline changed — re-review needed]";
                                    }
                                    pending.Steps[i].ReviewNote = note;
                                }
                            }
                        }
                    }
                }
            }

            // Write merged result
            var outputJson = JsonSerializer.Serialize(pending, SerializationOptions.Indented);
            await File.WriteAllTextAsync(baselinePath, outputJson, cancellationToken);
        }

        private static string GetJournalsDir()
        {
            var testAssets = ServerLocator.GetTestAssetsDir();
            return Path.Combine(testAssets, "journals");
        }

        /// <summary>
        /// Remove the .pending/ directory if empty.
        /// </summary>
        private static void CleanupPendingDir(string pendingDir)
        {
            if (Directory.Exists(pendingDir) && !Directory.EnumerateFileSystemEntries(pendingDir).Any())
            {
                Directory.Delete(pendingDir);
            }
        }
    }
}