namespace Microsoft.PowerPlatformLS.Tools.LspJournalCli.Models
{
    using System.Text.Json;
    using System.Text.Json.Serialization;

    /// <summary>
    /// A self-describing, self-validating journal file.
    /// The journal IS the test: it defines steps, carries expected responses,
    /// and after execution records actual responses with pass/fail status.
    /// </summary>
    public sealed class Journal
    {
        [JsonPropertyName("metadata")]
        public JournalMetadata Metadata { get; set; } = new();

        [JsonPropertyName("steps")]
        public List<JournalStep> Steps { get; set; } = [];
    }

    /// <summary>
    /// Journal metadata — provenance and classification.
    /// </summary>
    public sealed class JournalMetadata
    {
        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("workspaceRoot")]
        public string? WorkspaceRoot { get; set; }

        [JsonPropertyName("classification")]
        public string Classification { get; set; } = "recorded";

        [JsonPropertyName("branch")]
        public string? Branch { get; set; }

        [JsonPropertyName("commit")]
        public string? Commit { get; set; }

        [JsonPropertyName("timestamp")]
        public string? Timestamp { get; set; }

        /// <summary>
        /// The branch that the current branch diverged from (typically "main").
        /// Computed via git merge-base.
        /// </summary>
        [JsonPropertyName("branchBase")]
        public string? BranchBase { get; set; }

        /// <summary>
        /// Number of commits between the merge-base and HEAD.
        /// </summary>
        [JsonPropertyName("branchDepth")]
        public int? BranchDepth { get; set; }

        /// <summary>
        /// Required when classification is "normative". Explains why this behavior was promoted.
        /// </summary>
        [JsonPropertyName("normativeReason")]
        public string? NormativeReason { get; set; }

        /// <summary>
        /// Required when classification is "normative". Who signed off.
        /// </summary>
        [JsonPropertyName("normativeReviewer")]
        public string? NormativeReviewer { get; set; }
    }

    /// <summary>
    /// A single step in the journal. Carries the test definition (step, params, waitFor)
    /// and the validation state (expected, actual, status).
    /// </summary>
    public sealed class JournalStep
    {
        /// <summary>
        /// The step type: initialize, initialized, open, close, change, completion,
        /// diagnostics, shutdown, exit, waitForNotification, or any LSP method name.
        /// </summary>
        [JsonPropertyName("step")]
        public string Step { get; set; } = string.Empty;

        /// <summary>
        /// Parameters sent with the step.
        /// </summary>
        [JsonPropertyName("params")]
        public JsonElement? Params { get; set; }

        /// <summary>
        /// Expected response from the previous run. Used for inline validation.
        /// Absent on first run (recording baseline).
        /// </summary>
        [JsonPropertyName("expected")]
        public JsonElement? Expected { get; set; }

        /// <summary>
        /// Actual response from this run.
        /// </summary>
        [JsonPropertyName("actual")]
        public JsonElement? Actual { get; set; }

        /// <summary>
        /// Pass/fail status from inline validation.
        /// "pass" | "fail" | "recorded" (no expected to compare against).
        /// Null in a not-yet-run journal.
        /// </summary>
        [JsonPropertyName("status")]
        public string? Status { get; set; }

        /// <summary>
        /// Structured diff details when status is "fail".
        /// </summary>
        [JsonPropertyName("diff")]
        public List<DiffDetail>? Diff { get; set; }

        /// <summary>
        /// Notifications to wait for after this step executes.
        /// </summary>
        [JsonPropertyName("waitFor")]
        public List<WaitForSpec>? WaitFor { get; set; }

        /// <summary>
        /// Expected notifications from the previous run.
        /// </summary>
        [JsonPropertyName("expectedNotifications")]
        public List<JournalNotification>? ExpectedNotifications { get; set; }

        /// <summary>
        /// Actual notifications captured during this run.
        /// </summary>
        [JsonPropertyName("actualNotifications")]
        public List<JournalNotification>? ActualNotifications { get; set; }

        /// <summary>
        /// Review status: null (unreviewed), "confirmed", "suspect", or "stale".
        /// Human-authored — never written or cleared by the runner automatically.
        /// Preserved across re-records via fingerprint-based annotation merge.
        /// </summary>
        [JsonPropertyName("review")]
        public string? Review { get; set; }

        /// <summary>
        /// Free-text explanation of why this step is suspect, or what was confirmed.
        /// Human-authored, preserved across re-records.
        /// </summary>
        [JsonPropertyName("reviewNote")]
        public string? ReviewNote { get; set; }

        /// <summary>
        /// Optional cross-reference to a suspect-behavior ID (e.g., "S1")
        /// from the test matrix document.
        /// </summary>
        [JsonPropertyName("suspectId")]
        public string? SuspectId { get; set; }
    }

    /// <summary>
    /// A captured notification.
    /// </summary>
    public sealed class JournalNotification
    {
        [JsonPropertyName("method")]
        public string Method { get; set; } = string.Empty;

        [JsonPropertyName("params")]
        public JsonElement? Params { get; set; }
    }

    /// <summary>
    /// Describes a notification to wait for after a step.
    /// </summary>
    public sealed class WaitForSpec
    {
        [JsonPropertyName("method")]
        public string Method { get; set; } = string.Empty;

        [JsonPropertyName("timeoutMs")]
        public int TimeoutMs { get; set; } = 10_000;
    }

    /// <summary>
    /// A single value-level difference between expected and actual.
    /// </summary>
    public sealed class DiffDetail
    {
        [JsonPropertyName("path")]
        public string Path { get; set; } = string.Empty;

        [JsonPropertyName("expected")]
        public string? Expected { get; set; }

        [JsonPropertyName("actual")]
        public string? Actual { get; set; }

        [JsonPropertyName("kind")]
        public string Kind { get; set; } = "value_mismatch";
    }
}