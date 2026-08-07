namespace Microsoft.PowerPlatformLS.Contracts.Internal.Common
{
    /// <summary>
    /// Canonical telemetry dimension names used across the language server.
    /// All names use camelCase to align with Application Insights conventions.
    /// New dimensions MUST be added here before use in code.
    /// </summary>
    public static class TelemetryDimensions
    {
        // ─────────────────────────────────────────────────────────────────────
        // Shared dimensions (added to every event)
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Session correlation UUID shared between FE and BE.</summary>
        public const string SessionId = "sessionId";

        /// <summary>Language server process ID.</summary>
        public const string Pid = "pid";

        /// <summary>VSIX version string.</summary>
        public const string Version = "version";

        /// <summary>Git commit hash of the build.</summary>
        public const string GitHash = "gitHash";

        /// <summary>
        /// "true" when running in dev/debug mode. Added to every event so queries
        /// can filter out dev noise: <c>where customDimensions.isDevMode != "true"</c>
        /// </summary>
        public const string IsDevMode = "isDevMode";

        /// <summary>Connected agent unique ID.</summary>
        public const string AgentId = "agentId";

        /// <summary>Dataverse environment unique ID.</summary>
        public const string EnvironmentId = "environmentId";

        /// <summary>Operation duration in milliseconds.</summary>
        public const string DurationMs = "durationMs";

        // ─────────────────────────────────────────────────────────────────────
        // LSP request dimensions (per handler invocation)
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Internal LSP request correlation ID. Useful for filtering a session to diagnose one request.</summary>
        public const string ReqId = "reqId";

        /// <summary>LSP method name (e.g., powerplatformls/cloneAgent, textDocument/completion).</summary>
        public const string LspMethod = "lspMethod";

        /// <summary>Handler/operation result: completed | failed | canceled.</summary>
        public const string Outcome = "outcome";

        // ─────────────────────────────────────────────────────────────────────
        // HTTP request dimensions (per HTTP call in LoggingHttpHandler)
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>HTTP verb: GET | POST | PATCH | DELETE.</summary>
        public const string HttpMethod = "httpMethod";

        /// <summary>Normalized URL path with GUIDs replaced by {id}. No query string.</summary>
        public const string HttpEndpoint = "httpEndpoint";

        /// <summary>HTTP response status code (e.g., 200, 401, 500).</summary>
        public const string HttpStatusCode = "httpStatusCode";

        /// <summary>HTTP reason phrase on non-2xx responses (e.g., "Forbidden", "Not Found").</summary>
        public const string HttpReason = "httpReason";

        // ─────────────────────────────────────────────────────────────────────
        // Sync operation dimensions
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Internal sync function being executed (e.g., FetchRemoteAgent, WriteWorkspace).</summary>
        public const string SyncFunction = "syncFunction";

        // ─────────────────────────────────────────────────────────────────────
        // Error/diagnostic dimensions
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Error message from failed handler or exception. Scrubbed by PiiScrubTelemetryInitializer.</summary>
        public const string ErrorMessage = "errorMessage";
    }
}
