namespace Microsoft.PowerPlatformLS.Tools.LspJournalCli.Commands
{
    using System.Text.Json;
    using Microsoft.PowerPlatformLS.Tools.LspJournalCli.Execution;
    using Microsoft.PowerPlatformLS.Tools.LspJournalCli.Models;
    using Microsoft.PowerPlatformLS.Tools.LspJournalCli.Transport;

    /// <summary>
    /// The single operation: execute a journal's steps, validate inline against expected
    /// responses, and write a self-validating journal only when material changes are detected.
    /// A passing run produces zero git diff.
    /// </summary>
    public static class RunCommand
    {
        /// <summary>
        /// Result of running a single journal — used by RunAllAsync for summary reporting.
        /// </summary>
        private sealed record JournalRunResult(string Name, int ExitCode, bool HasPending, string? PendingPath);

        /// <summary>
        /// Run a journal by name. Resolves paths from TestAssets conventions.
        /// </summary>
        public static async Task<int> RunByNameAsync(
            string name,
            bool verbose = false,
            bool force = false,
            CancellationToken cancellationToken = default)
        {
            var testAssets = ServerLocator.GetTestAssetsDir();
            var journalPath = Path.Combine(testAssets, "journals", $"{name}.journal.json");

            if (!File.Exists(journalPath))
            {
                Console.Error.WriteLine($"Journal not found: {journalPath}");
                return 1;
            }

            return await RunJournalAsync(journalPath, verbose, force, cancellationToken);
        }

        /// <summary>
        /// Run all journals in the TestAssets/journals directory.
        /// </summary>
        public static async Task<int> RunAllAsync(
            bool verbose = false,
            bool force = false,
            CancellationToken cancellationToken = default)
        {
            var testAssets = ServerLocator.GetTestAssetsDir();
            var journalDir = Path.Combine(testAssets, "journals");

            if (!Directory.Exists(journalDir))
            {
                Console.Error.WriteLine($"Journals directory not found: {journalDir}");
                return 1;
            }

            var journals = Directory.GetFiles(journalDir, "*.journal.json")
                .OrderBy(f => f)
                .ToArray();

            if (journals.Length == 0)
            {
                Console.Error.WriteLine("No journals found.");
                return 1;
            }

            Console.WriteLine($"Running {journals.Length} journal(s)...\n");
            var results = new List<JournalRunResult>();

            if (force) Console.WriteLine("(--force: writing all journals to .pending/)\n");

            foreach (var journalPath in journals)
            {
                var result = await RunJournalAsync(journalPath, verbose, force, cancellationToken);
                var name = Path.GetFileNameWithoutExtension(journalPath).Replace(".journal", "");
                var pendingPath = AcceptCommand.GetPendingPath(journalPath);
                var hasPending = File.Exists(pendingPath);
                results.Add(new JournalRunResult(name, result, hasPending, hasPending ? pendingPath : null));
                Console.WriteLine();
            }

            // Summary
            var totalFailed = results.Count(r => r.ExitCode != 0);
            var totalPending = results.Count(r => r.HasPending);
            Console.WriteLine($"Done. {journals.Length - totalFailed}/{journals.Length} passed.");

            if (totalPending > 0)
            {
                Console.WriteLine($"Pending changes: {totalPending} journal(s) in .pending/");
                foreach (var r in results.Where(r => r.HasPending))
                {
                    Console.WriteLine($"  {r.Name}");
                }
                Console.WriteLine($"  dotnet run -- accept --all   to accept");
                Console.WriteLine($"  dotnet run -- discard --all  to discard");
            }

            return totalFailed > 0 ? 1 : 0;
        }

        /// <summary>
        /// Execute a journal file: run every step, validate inline, write to .pending/ only on material change.
        /// When <paramref name="force"/> is true, always write to .pending/ (e.g. for metadata schema updates).
        /// </summary>
        public static async Task<int> RunJournalAsync(
            string journalPath,
            bool verbose = false,
            bool force = false,
            CancellationToken cancellationToken = default)
        {
            var name = Path.GetFileNameWithoutExtension(journalPath).Replace(".journal", "");
            Console.WriteLine($"--- {name} ---");

            // Load journal
            var json = await File.ReadAllTextAsync(journalPath, cancellationToken);
            var journal = JsonSerializer.Deserialize<Journal>(json, SerializationOptions.Default)
                ?? throw new InvalidOperationException($"Failed to deserialize: {journalPath}");

            // Snapshot original expected values for material-change detection
            var originalExpected = SnapshotExpected(journal.Steps);

            // Resolve workspace URI and expand ${workspace} → absolute URIs in params
            var resolvedWorkspaceUri = ResolveWorkspaceUri(journal, journalPath);
            var resolvedWorkspacePath = ResolveWorkspacePath(journal, journalPath);
            if (resolvedWorkspaceUri is not null)
            {
                foreach (var step in journal.Steps)
                {
                    if (step.Params.HasValue)
                    {
                        var raw = UriPlaceholder.Expand(step.Params.Value.GetRawText(), resolvedWorkspaceUri);
                        step.Params = JsonDocument.Parse(raw).RootElement.Clone();
                    }
                }
            }

            if (resolvedWorkspacePath is not null)
            {
                foreach (var step in journal.Steps)
                {
                    if (step.Params.HasValue)
                    {
                        step.Params = DocumentTextPolicy.PrepareParamsForExecution(step.Params.Value, resolvedWorkspacePath);
                    }
                }
            }

            // Locate and start server
            var serverPath = ServerLocator.FindServer();
            if (verbose)
            {
                Console.WriteLine($"  Server: {serverPath}");
            }

            await using var server = new LspServerProcess(serverPath, []) { Verbose = verbose };
            await server.StartAsync(cancellationToken);

            var executor = new StepExecutor(server.Transport);

            // Execute each step
            var passed = 0;
            var failed = 0;
            var recorded = 0;
            var warnings = 0;

            for (var i = 0; i < journal.Steps.Count; i++)
            {
                var step = journal.Steps[i];
                Console.Write($"  [{i + 1}/{journal.Steps.Count}] {step.Step}...");

                try
                {
                    // Execute
                    var (response, notifications) = await ExecuteStepAsync(executor, step, cancellationToken);

                    // Normalize
                    response = OutputNormalizer.Normalize(response);

                    // Relativize absolute URIs → ${workspace} in actual response and notifications
                    if (resolvedWorkspaceUri is not null)
                    {
                        if (response.HasValue)
                        {
                            var relJson = UriPlaceholder.Relativize(response.Value.GetRawText(), resolvedWorkspaceUri);
                            response = JsonDocument.Parse(relJson).RootElement.Clone();
                        }

                        if (notifications is not null)
                        {
                            for (var n = 0; n < notifications.Count; n++)
                            {
                                var notif = notifications[n];
                                if (notif.Params.HasValue)
                                {
                                    var relJson = UriPlaceholder.Relativize(notif.Params.Value.GetRawText(), resolvedWorkspaceUri);
                                    notif.Params = JsonDocument.Parse(relJson).RootElement.Clone();
                                }
                            }
                        }
                    }

                    // Validate inline
                    if (step.Expected.HasValue || step.ExpectedNotifications is { Count: > 0 })
                    {
                        var diffs = step.Expected.HasValue
                            ? CompareResponses(step.Expected.Value, response)
                            : new List<DiffDetail>();
                        var (notifErrors, notifWarnings) = CompareNotificationsWithSeverity(step.ExpectedNotifications, notifications);
                        diffs.AddRange(notifErrors);

                        if (diffs.Count == 0)
                        {
                            step.Status = "pass";
                            passed++;

                            if (notifWarnings.Count > 0)
                            {
                                warnings++;
                                Console.WriteLine(" pass (with warnings)");
                                foreach (var w in notifWarnings)
                                {
                                    Console.ForegroundColor = ConsoleColor.Yellow;
                                    Console.WriteLine($"    warning: {w.Kind}: {w.Path}");
                                    if (w.Actual is not null) Console.WriteLine($"      {w.Actual}");
                                    Console.ResetColor();
                                }
                            }
                            else
                            {
                                Console.WriteLine(" pass");
                            }
                        }
                        else
                        {
                            step.Status = "fail";
                            step.Diff = diffs;
                            failed++;
                            Console.WriteLine(" FAIL");
                            foreach (var d in diffs)
                            {
                                Console.WriteLine($"    {d.Kind}: {d.Path}");
                                if (d.Expected is not null) Console.WriteLine($"      expected: {Truncate(d.Expected, 120)}");
                                if (d.Actual is not null) Console.WriteLine($"      actual:   {Truncate(d.Actual, 120)}");
                            }
                        }
                    }
                    else if (response.HasValue || (notifications is { Count: > 0 }))
                    {
                        // Truly new recording: step had no expectations but produced results
                        step.Status = "recorded";
                        recorded++;
                        Console.WriteLine(" RECORDED (new baseline)");
                    }
                    else
                    {
                        // Fire-and-forget step (e.g. initialized, exit) — no expectations, no response
                        step.Status = "pass";
                        passed++;
                        Console.WriteLine(" ok");
                    }

                    // Store actual
                    step.Actual = response;
                    step.ActualNotifications = notifications;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    var msg = $"Timed out after {StepExecutor.DefaultRequestTimeoutMs}ms";
                    Console.WriteLine($" FAIL: {msg}");
                    WriteServerStderrSummary(server, step.Step, maxLines: 40);
                    step.Actual = JsonSerializer.SerializeToElement(new { error = msg }, SerializationOptions.Default);
                    step.Status = step.Expected.HasValue ? "fail" : "recorded";
                    step.Diff = step.Expected.HasValue
                        ? [new DiffDetail { Kind = "timeout", Path = "response", Actual = msg }]
                        : null;
                    if (step.Expected.HasValue) failed++; else recorded++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($" FAIL: {ex.Message}");
                    WriteServerStderrSummary(server, step.Step, maxLines: 40);
                    step.Actual = JsonSerializer.SerializeToElement(new { error = ex.Message }, SerializationOptions.Default);
                    step.Status = step.Expected.HasValue ? "fail" : "recorded";
                    step.Diff = step.Expected.HasValue
                        ? [new DiffDetail { Kind = "exception", Path = "response", Actual = ex.Message }]
                        : null;
                    if (step.Expected.HasValue) failed++; else recorded++;
                }
            }

            // Stop server
            server.ShutdownAlreadySent = executor.ShutdownSent;
            await server.StopAsync(skipShutdownSequence: executor.ShutdownSent);

            // Detect material change by comparing actual results to original expected
            var hasMaterialChange = force || DetectMaterialChange(originalExpected, journal.Steps);

            if (hasMaterialChange)
            {
                // Stamp provenance (only on material change)
                journal.Metadata.Branch = GitMetadata.GetBranch();
                journal.Metadata.Commit = GitMetadata.GetCommit();
                journal.Metadata.Timestamp = DateTime.UtcNow.ToString("o");
                journal.Metadata.BranchBase = GitMetadata.GetBranchBase();
                journal.Metadata.BranchDepth = GitMetadata.GetBranchDepth();

                // Promote actual → expected for baseline
                foreach (var step in journal.Steps)
                {
                    // Relativize params back to ${workspace} form before writing
                    if (resolvedWorkspacePath is not null && step.Params.HasValue)
                    {
                        step.Params = DocumentTextPolicy.ScrubParamsForStorage(step.Params.Value, resolvedWorkspacePath);
                    }

                    if (resolvedWorkspaceUri is not null && step.Params.HasValue)
                    {
                        var relParams = UriPlaceholder.Relativize(step.Params.Value.GetRawText(), resolvedWorkspaceUri);
                        step.Params = JsonDocument.Parse(relParams).RootElement.Clone();
                    }

                    step.Expected = step.Actual;
                    step.ExpectedNotifications = step.ActualNotifications;
                    // Clear transient fields
                    step.Actual = null;
                    step.ActualNotifications = null;
                    step.Status = null;
                    step.Diff = null;
                }

                // Write to .pending/ — never overwrite baseline in place
                var journalsDir = Path.GetDirectoryName(journalPath) ?? Path.GetFullPath(".");
                AcceptCommand.EnsurePendingDir(journalsDir);
                var pendingPath = AcceptCommand.GetPendingPath(journalPath);
                var outputJson = JsonSerializer.Serialize(journal, SerializationOptions.Indented);
                await File.WriteAllTextAsync(pendingPath, outputJson, cancellationToken);

                Console.WriteLine($"  Pending: .pending/{Path.GetFileName(journalPath)}");
            }
            // else: no write — file on disk is unchanged

            // Summary
            var total = journal.Steps.Count;
            var statusLabel = failed > 0 ? "FAILED" : "passed";
            Console.Write($"  Result: {statusLabel}");
            if (failed > 0) Console.Write($" ({failed} failed)");
            if (recorded > 0) Console.Write($" ({recorded} newly recorded)");
            if (warnings > 0) Console.Write($" ({warnings} warnings)");
            if (!hasMaterialChange && failed == 0) Console.Write($" (unchanged — no write)");
            Console.WriteLine($" [{passed + recorded}/{total}]");

            // Server exit info
            var exitInfo = server.GetProcessInfo();
            if (exitInfo is not null && exitInfo.Contains("still running"))
            {
                Console.WriteLine($"  Warning: server process {exitInfo}");
            }

            return failed > 0 ? 1 : 0;
        }

        /// <summary>
        /// Execute a single step and return (response, notifications).
        /// </summary>
        private static async Task<(JsonElement? Response, List<JournalNotification>? Notifications)> ExecuteStepAsync(
            StepExecutor executor,
            JournalStep step,
            CancellationToken cancellationToken)
        {
            var result = await executor.ExecuteStepAsync(step, cancellationToken);
            return (result.Response, result.Notifications);
        }

        /// <summary>
        /// Compare expected and actual responses, returning any diffs.
        /// </summary>
        private static List<DiffDetail> CompareResponses(JsonElement expected, JsonElement? actual)
        {
            var diffs = new List<DiffDetail>();

            if (!actual.HasValue)
            {
                diffs.Add(new DiffDetail
                {
                    Kind = "missing_response",
                    Path = "response",
                    Expected = Truncate(expected.GetRawText(), 200),
                });
                return diffs;
            }

            var expectedNorm = OutputNormalizer.NormalizeToString(expected);
            var actualNorm = OutputNormalizer.NormalizeToString(actual.Value);

            if (expectedNorm != actualNorm)
            {
                diffs.Add(new DiffDetail
                {
                    Kind = "value_mismatch",
                    Path = "response",
                    Expected = Truncate(expectedNorm, 200),
                    Actual = Truncate(actualNorm, 200),
                });
            }

            return diffs;
        }

        /// <summary>
        /// Compare expected and actual notification lists, separating errors from warnings.
        /// Order is ignored; matching is based on method + normalized params (multiset).
        /// Extra unexpected notifications are warnings (not errors).
        /// </summary>
        private static (List<DiffDetail> Errors, List<DiffDetail> Warnings) CompareNotificationsWithSeverity(
            List<JournalNotification>? expected,
            List<JournalNotification>? actual)
        {
            var errors = new List<DiffDetail>();
            var warnings = new List<DiffDetail>();

            if (expected is null || expected.Count == 0)
            {
                // No expected notifications — extra actuals are warnings only
                if (actual is not null)
                {
                    for (var i = 0; i < actual.Count; i++)
                    {
                        warnings.Add(new DiffDetail
                        {
                            Kind = "extra_notification",
                            Path = "notifications",
                            Actual = BuildNotificationKey(actual[i]),
                        });
                    }
                }
                return (errors, warnings);
            }

            if (actual is null || actual.Count == 0)
            {
                errors.Add(new DiffDetail
                {
                    Kind = "missing_notifications",
                    Path = "notifications",
                    Expected = $"{expected.Count} notification(s)",
                });
                return (errors, warnings);
            }

            var expectedCounts = BuildNotificationCounts(expected);
            var actualCounts = BuildNotificationCounts(actual);

            foreach (var (key, expectedCount) in expectedCounts)
            {
                actualCounts.TryGetValue(key, out var actualCount);
                var missing = expectedCount - actualCount;
                if (missing > 0)
                {
                    errors.Add(new DiffDetail
                    {
                        Kind = "missing_notification",
                        Path = "notifications",
                        Expected = missing == 1 ? key : $"{key} (x{missing})",
                    });
                }
            }

            foreach (var (key, actualCount) in actualCounts)
            {
                expectedCounts.TryGetValue(key, out var expectedCount);
                var extra = actualCount - expectedCount;
                if (extra > 0)
                {
                    warnings.Add(new DiffDetail
                    {
                        Kind = "extra_notification",
                        Path = "notifications",
                        Actual = extra == 1 ? key : $"{key} (x{extra})",
                    });
                }
            }

            return (errors, warnings);
        }

        /// <summary>
        /// Snapshot the original expected response and expectedNotifications for each step.
        /// Used to detect whether a run produced material changes.
        /// </summary>
        private sealed record StepSnapshot(string? ExpectedNorm, Dictionary<string, int>? ExpectedNotifications);

        private static List<StepSnapshot> SnapshotExpected(List<JournalStep> steps)
        {
            var snapshots = new List<StepSnapshot>(steps.Count);

            foreach (var step in steps)
            {
                string? expectedNorm = null;
                if (step.Expected.HasValue)
                {
                    expectedNorm = OutputNormalizer.NormalizeToString(step.Expected.Value);
                }

                Dictionary<string, int>? expectedNotifs = null;
                if (step.ExpectedNotifications is { Count: > 0 })
                {
                    expectedNotifs = BuildNotificationCounts(step.ExpectedNotifications);
                }

                snapshots.Add(new StepSnapshot(expectedNorm, expectedNotifs));
            }

            return snapshots;
        }

        /// <summary>
        /// Detect whether any step's actual response/notifications materially differ
        /// from its original expected values. Uses normalized comparison to avoid
        /// false positives from non-deterministic fields.
        /// </summary>
        private static bool DetectMaterialChange(List<StepSnapshot> originalSnapshots, List<JournalStep> steps)
        {
            // Step count changed (shouldn't happen in a run, but be safe)
            if (originalSnapshots.Count != steps.Count)
                return true;

            for (var i = 0; i < steps.Count; i++)
            {
                var original = originalSnapshots[i];
                var step = steps[i];

                // New recording — step had no expected, now has an actual
                if (original.ExpectedNorm is null && step.Actual.HasValue)
                    return true;

                // Compare response
                if (original.ExpectedNorm is not null && !step.Actual.HasValue)
                    return true;

                if (original.ExpectedNorm is not null && step.Actual.HasValue)
                {
                    var actualNorm = OutputNormalizer.NormalizeToString(step.Actual.Value);
                    if (original.ExpectedNorm != actualNorm)
                        return true;
                }

                // Compare notifications (ignore ordering; extras are material)
                if (HasNotificationMaterialChange(original.ExpectedNotifications, step.ActualNotifications))
                    return true;
            }

            return false;
        }

        private static string BuildNotificationKey(JournalNotification notification)
        {
            if (notification.Params.HasValue)
                return notification.Method + ":" + OutputNormalizer.NormalizeToString(notification.Params.Value);
            return notification.Method;
        }

        private static Dictionary<string, int> BuildNotificationCounts(List<JournalNotification> notifications)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var notification in notifications)
            {
                var key = BuildNotificationKey(notification);
                counts.TryGetValue(key, out var count);
                counts[key] = count + 1;
            }

            return counts;
        }

        internal static bool HasNotificationMaterialChange(
            Dictionary<string, int>? expectedCounts,
            List<JournalNotification>? actualNotifications)
        {
            if (expectedCounts is null || expectedCounts.Count == 0)
                return actualNotifications is { Count: > 0 };

            if (actualNotifications is null || actualNotifications.Count == 0)
                return true;

            var actualCounts = BuildNotificationCounts(actualNotifications);

            // Only MISSING expected notifications are material changes.
            // Extra unexpected notifications are warnings only when the step
            // already expects notifications. If a step expects none, any
            // notifications are treated as material changes.
            foreach (var (key, expectedCount) in expectedCounts)
            {
                actualCounts.TryGetValue(key, out var actualCount);
                if (actualCount < expectedCount)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Compute the absolute file:/// URI for the workspace root described by
        /// <see cref="JournalMetadata.WorkspaceRoot"/> relative to the journal file.
        /// Returns null when the journal has no workspace root.
        /// </summary>
        private static string? ResolveWorkspaceUri(Journal journal, string journalFilePath)
        {
            var workspaceRoot = journal.Metadata.WorkspaceRoot;
            if (string.IsNullOrEmpty(workspaceRoot)) return null;

            var journalDir = Path.GetDirectoryName(Path.GetFullPath(journalFilePath)) ?? ".";
            var resolvedRoot = Path.GetFullPath(Path.Combine(journalDir, workspaceRoot));
            return new Uri(resolvedRoot).ToString().TrimEnd('/');
        }

        private static string? ResolveWorkspacePath(Journal journal, string journalFilePath)
        {
            var workspaceRoot = journal.Metadata.WorkspaceRoot;
            if (string.IsNullOrEmpty(workspaceRoot)) return null;

            var journalDir = Path.GetDirectoryName(Path.GetFullPath(journalFilePath)) ?? ".";
            return Path.GetFullPath(Path.Combine(journalDir, workspaceRoot));
        }

        private static string Truncate(string s, int max) =>
            s.Length <= max ? s : s[..max] + "...";

        private static void WriteServerStderrSummary(LspServerProcess server, string stepName, int maxLines)
        {
            var stderr = server.StderrLines;
            if (stderr.Count == 0) return;

            var start = Math.Max(0, stderr.Count - maxLines);
            Console.WriteLine($"  Server stderr (last {stderr.Count - start} lines) after {stepName}:");
            for (var i = start; i < stderr.Count; i++)
            {
                Console.WriteLine($"    {stderr[i]}");
            }
        }
    }
}