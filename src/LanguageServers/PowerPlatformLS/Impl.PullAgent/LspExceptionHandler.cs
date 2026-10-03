namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.Agents.Platform.Content.Exceptions;
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.CopilotStudio.Sync;
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Net.Http;
    using System.Threading;

    /// <summary>
    /// Classifies exceptions and logs them at the appropriate level.
    /// Provides consistent error handling across all LSP request handlers.
    /// Each error includes [ExceptionType] and source location (at File.cs:Line) for traceability.
    /// Exceptions already logged by instrumented layers (HTTP, Sync) are not duplicated.
    /// Exceptions whose message carries workspace file paths are returned to the client without logging.
    /// </summary>
    internal static class LspExceptionHandler
    {
        private const string WorkspaceContentPiiType = "WORKSPACE FILE DETAILS";

        /// <summary>
        /// Classifies the exception, logs it at the appropriate severity, and returns
        /// a status code and user-facing message suitable for the LSP response.
        /// </summary>
        public static (int Code, string Message) Handle(Exception ex, ILspLogger logger, CancellationToken cancellationToken = default)
        {
            return ex switch
            {
                // No logging — already logged by an instrumented layer or propagated to FE directly.
                HttpRequestException hre =>
                    NoLog(MapHttpStatusCode(hre), hre.Message),

                FileNotFoundException fnf =>
                    NoLog(400, fnf.Message),

                DirectoryNotFoundException dnf =>
                    NoLog(400, dnf.Message),

                WorkspaceValidationException wve =>
                    NoLog(400, DescribeWorkspaceValidation(wve)),

                InvalidOperationException ioe =>
                    NoLog(400, ioe.Message),

                OperationCanceledException when cancellationToken.IsCancellationRequested =>
                    NoLog(499, "Operation was cancelled."),

                OperationCanceledException =>
                    NoLog(504, "Operation timed out."),

                // Logged — service or unexpected errors go to exceptions table.
                DataverseBadRequestException dbre =>
                    LogAsException(logger, dbre, code: dbre.StatusCode),

                DataverseServiceUnavailableException dsue =>
                    LogAsException(logger, dsue, code: 503, message: "The Copilot Studio service is temporarily unavailable. Please try again in a moment."),

                _ => LogAsException(logger, ex),
            };
        }

        /// <summary>
        /// Returns code/message without logging (exception was already logged by an instrumented layer).
        /// </summary>
        private static (int Code, string Message) NoLog(int code, string message)
        {
            return (code, message);
        }

        /// <summary>
        /// Workspace diagnostics name files and quote their content, both of which can carry
        /// user-authored names. Tagging them keeps the detail visible in the client UI while the
        /// client and host telemetry scrubbers replace it with a redaction marker.
        /// </summary>
        private static string DescribeWorkspaceValidation(WorkspaceValidationException failure)
            => WorkspaceValidationException.Describe(failure.Diagnostics, static diagnostic => diagnostic.ToString(ProtectUserContent));

        /// <summary>
        /// Formats the files a pull left with unresolved conflict markers as a non-fatal warning,
        /// using the same user-content tagging as validation failures.
        /// </summary>
        internal static string DescribeConflicts(IReadOnlyCollection<WorkspaceDiagnostic> conflicts)
            => conflicts.Count == 0
                ? string.Empty
                : WorkspaceDiagnostic.Summarize(
                    count => $"{count} workspace {(count == 1 ? "file has" : "files have")} unresolved merge conflicts. Resolve them before pushing:",
                    conflicts,
                    static diagnostic => diagnostic.ToString(ProtectUserContent));

        private static string ProtectUserContent(string value)
            => $"<pii type=\"{WorkspaceContentPiiType}\" encoded=\"true\">{value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")}</pii>";

        /// <summary>
        /// Logs to exceptions table (full stack trace).
        /// Defaults to code 500 and ex.Message if not overridden.
        /// </summary>
        private static (int Code, string Message) LogAsException(ILspLogger logger, Exception ex, int code = 500, string? message = null)
        {
            logger.LogException(ex);
            return (code, message ?? ex.Message);
        }

        private static int MapHttpStatusCode(HttpRequestException hre)
        {
            return hre.StatusCode.HasValue ? (int)hre.StatusCode.Value : 502;
        }
    }
}
