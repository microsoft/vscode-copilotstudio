namespace Microsoft.PowerPlatformLS.Tools.LspJournalCli.Commands
{
    using System.Text.Json;
    using Microsoft.PowerPlatformLS.Tools.LspJournalCli.Execution;
    using Microsoft.PowerPlatformLS.Tools.LspJournalCli.Models;
    using Microsoft.PowerPlatformLS.Tools.LspJournalCli.Transport;

    /// <summary>
    /// Compares two V2 journal files and reports structured diffs.
    /// </summary>
    public sealed class DiffCommand
    {
        /// <summary>
        /// Run the diff command: compare two journal files step by step.
        /// </summary>
        public static async Task<int> RunAsync(FileInfo journalA, FileInfo journalB, CancellationToken cancellationToken = default)
        {
            Console.WriteLine($"Comparing journals:");
            Console.WriteLine($"  A: {journalA.FullName}");
            Console.WriteLine($"  B: {journalB.FullName}");

            var jsonA = await File.ReadAllTextAsync(journalA.FullName, cancellationToken);
            var jsonB = await File.ReadAllTextAsync(journalB.FullName, cancellationToken);

            var a = JsonSerializer.Deserialize<Journal>(jsonA, SerializationOptions.Default)
                ?? throw new InvalidOperationException($"Failed to deserialize journal: {journalA.FullName}");
            var b = JsonSerializer.Deserialize<Journal>(jsonB, SerializationOptions.Default)
                ?? throw new InvalidOperationException($"Failed to deserialize journal: {journalB.FullName}");

            // Report metadata differences
            Console.WriteLine("\nMetadata:");
            Console.WriteLine($"  A: classification={a.Metadata.Classification}, commit={a.Metadata.Commit}");
            Console.WriteLine($"  B: classification={b.Metadata.Classification}, commit={b.Metadata.Commit}");

            // Compare steps
            var maxSteps = Math.Max(a.Steps.Count, b.Steps.Count);
            var matched = 0;
            var mismatched = 0;
            var diffs = new List<(int Index, string Step, List<DiffDetail> Details)>();

            for (var i = 0; i < maxSteps; i++)
            {
                if (i >= a.Steps.Count)
                {
                    mismatched++;
                    diffs.Add((i, b.Steps[i].Step, [new DiffDetail
                {
                    Kind = "extra_step",
                    Path = $"steps[{i}]",
                    Actual = b.Steps[i].Step,
                }]));
                    continue;
                }

                if (i >= b.Steps.Count)
                {
                    mismatched++;
                    diffs.Add((i, a.Steps[i].Step, [new DiffDetail
                {
                    Kind = "missing_step",
                    Path = $"steps[{i}]",
                    Expected = a.Steps[i].Step,
                }]));
                    continue;
                }

                var stepDiffs = CompareSteps(a.Steps[i], b.Steps[i], i);
                if (stepDiffs.Count == 0)
                {
                    matched++;
                }
                else
                {
                    mismatched++;
                    diffs.Add((i, a.Steps[i].Step, stepDiffs));
                }
            }

            Console.WriteLine($"\nResults:");
            Console.WriteLine($"  Total steps: {maxSteps}");
            Console.WriteLine($"  Matched:     {matched}");
            Console.WriteLine($"  Mismatched:  {mismatched}");

            if (mismatched == 0)
            {
                Console.WriteLine("\n*** IDENTICAL: journals match ***");
                return 0;
            }

            Console.WriteLine("\nDifferences:");
            foreach (var (index, step, details) in diffs)
            {
                Console.WriteLine($"\n  Step [{index}] ({step}):");
                foreach (var detail in details)
                {
                    Console.WriteLine($"    {detail.Kind} at {detail.Path}");
                    if (detail.Expected is not null)
                    {
                        Console.WriteLine($"      A: {detail.Expected}");
                    }

                    if (detail.Actual is not null)
                    {
                        Console.WriteLine($"      B: {detail.Actual}");
                    }
                }
            }

            return 1;
        }

        private static List<DiffDetail> CompareSteps(JournalStep a, JournalStep b, int index)
        {
            var diffs = new List<DiffDetail>();
            var basePath = $"steps[{index}]";

            if (a.Step != b.Step)
            {
                diffs.Add(new DiffDetail
                {
                    Kind = "value_mismatch",
                    Path = $"{basePath}.step",
                    Expected = a.Step,
                    Actual = b.Step,
                });
            }

            // Compare expected responses
            CompareJsonFields(a.Expected, b.Expected, $"{basePath}.expected", diffs);

            // Compare expected notifications
            CompareNotificationLists(a.ExpectedNotifications, b.ExpectedNotifications, $"{basePath}.expectedNotifications", diffs);

            return diffs;
        }

        private static void CompareJsonFields(JsonElement? a, JsonElement? b, string path, List<DiffDetail> diffs)
        {
            var aNorm = a.HasValue ? OutputNormalizer.NormalizeToString(a.Value) : null;
            var bNorm = b.HasValue ? OutputNormalizer.NormalizeToString(b.Value) : null;

            if (aNorm == bNorm) return;

            diffs.Add(new DiffDetail
            {
                Kind = aNorm is null ? "added" : bNorm is null ? "removed" : "value_mismatch",
                Path = path,
                Expected = aNorm is not null ? Truncate(aNorm) : null,
                Actual = bNorm is not null ? Truncate(bNorm) : null,
            });
        }

        private static void CompareNotificationLists(
            List<JournalNotification>? a,
            List<JournalNotification>? b,
            string basePath,
            List<DiffDetail> diffs)
        {
            var aCount = a?.Count ?? 0;
            var bCount = b?.Count ?? 0;
            var maxCount = Math.Max(aCount, bCount);

            for (var i = 0; i < maxCount; i++)
            {
                if (i >= aCount)
                {
                    // b is guaranteed non-null when bCount > aCount and i < maxCount
                    diffs.Add(new DiffDetail { Kind = "extra_notification", Path = $"{basePath}[{i}]", Actual = b?[i].Method });
                    continue;
                }

                if (i >= bCount)
                {
                    // a is guaranteed non-null when aCount > bCount and i < maxCount
                    diffs.Add(new DiffDetail { Kind = "missing_notification", Path = $"{basePath}[{i}]", Expected = a?[i].Method });
                    continue;
                }

                // Both a and b are non-null at this point since i < both counts
                var aN = a?[i];
                var bN = b?[i];

                if (aN?.Method != bN?.Method)
                {
                    diffs.Add(new DiffDetail
                    {
                        Kind = "notification_method_mismatch",
                        Path = $"{basePath}[{i}].method",
                        Expected = aN?.Method,
                        Actual = bN?.Method,
                    });
                }

                CompareJsonFields(aN?.Params, bN?.Params, $"{basePath}[{i}].params", diffs);
            }
        }

        private static string Truncate(string s, int max = 200) =>
            s.Length <= max ? s : s[..max] + "...";
    }
}