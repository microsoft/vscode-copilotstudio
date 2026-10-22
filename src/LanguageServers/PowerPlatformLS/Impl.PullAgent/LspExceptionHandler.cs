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

        private const string WorkflowNamePiiType = "WORKFLOW NAMES";

        private const string EnvironmentIdPiiType = "ENVIRONMENT ID";

        /// <summary>
        /// Classifies the exception, logs it at the appropriate severity, and returns
        /// a status code and user-facing message suitable for the LSP response.
        /// </summary>
        public static (int Code, string Message) Handle(Exception ex, ILspLogger logger, CancellationToken cancellationToken = default)
        {
            return ex switch
            {
                HttpRequestException hre =>
                    NoLog(MapHttpStatusCode(hre), hre.Message),

                FileNotFoundException fnf =>
                    NoLog(400, fnf.Message),

                DirectoryNotFoundException dnf =>
                    NoLog(400, dnf.Message),

                WorkspaceValidationException wve =>
                    NoLog(400, DescribeWorkspaceValidation(wve)),

                StandaloneWorkflowException swe when HoldsUnpushedChanges(swe.Failure) =>
                    NoLog(409, DescribeStandaloneWorkflow(swe)),

                StandaloneWorkflowException swe when RequiresUserAction(swe.Failure) =>
                    NoLog(400, DescribeStandaloneWorkflow(swe)),

                InvalidOperationException ioe =>
                    NoLog(400, ioe.Message),

                OperationCanceledException when cancellationToken.IsCancellationRequested =>
                    NoLog(499, "Operation was cancelled."),

                OperationCanceledException =>
                    NoLog(504, "Operation timed out."),

                DataverseBadRequestException dbre =>
                    LogAsException(logger, dbre, code: dbre.StatusCode),

                DataverseServiceUnavailableException dsue =>
                    LogAsException(logger, dsue, code: 503, message: "The Copilot Studio service is temporarily unavailable. Please try again in a moment."),

                StandaloneWorkflowException swe =>
                    LogAsException(logger, swe, message: DescribeStandaloneWorkflow(swe)),

                _ => LogAsException(logger, ex),
            };
        }

        /// <summary>
        /// Whether a standalone workflow failure means the workspace holds an edit the environment
        /// has not seen, which the client resolves by asking the user before replacing it.
        /// </summary>
        private static bool HoldsUnpushedChanges(StandaloneWorkflowFailure failure) =>
            failure is StandaloneWorkflowFailure.WorkspaceHoldsUnpushedChanges or StandaloneWorkflowFailure.ProjectHoldsUnpushedChanges;

        private static bool RequiresUserAction(StandaloneWorkflowFailure failure) => failure is StandaloneWorkflowFailure.InvalidName or StandaloneWorkflowFailure.Managed or StandaloneWorkflowFailure.EnvironmentHoldsUnpublishedDraft or StandaloneWorkflowFailure.EnvironmentHoldsUnpulledChanges;

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

        /// <summary>
        /// Standalone workflow failures name folders the service derived from a workflow's display
        /// name, and the environment they happened in. Neither survives the client's own matching,
        /// which only knows the display name, so they are tagged here: the detail stays visible in
        /// the client UI while both telemetry scrubbers replace it with a redaction marker.
        /// </summary>
        private static string DescribeStandaloneWorkflow(StandaloneWorkflowException failure)
        {
            var message = ProtectValue(failure.Message, failure.Subject, SubjectPiiType(failure.Failure));

            foreach (var folderName in failure.FolderNames)
            {
                message = ProtectValue(message, folderName, WorkflowNamePiiType);
            }

            return ProtectValue(message, failure.EnvironmentId, EnvironmentIdPiiType);
        }

        /// <summary>
        /// Names what a failure's subject holds, so a workspace path is not reported as a workflow
        /// name. A subject that is only an identifier the service assigned is left alone.
        /// </summary>
        private static string? SubjectPiiType(StandaloneWorkflowFailure failure) =>
            failure switch
            {
                StandaloneWorkflowFailure.NotFound or StandaloneWorkflowFailure.MetadataUnreadable => WorkspaceContentPiiType,
                StandaloneWorkflowFailure.SourceNotFound => null,
                _ => WorkflowNamePiiType,
            };

        private static string ProtectValue(string message, string? value, string? piiType) =>
            piiType is null || string.IsNullOrWhiteSpace(value) || !message.Contains(value!, StringComparison.Ordinal)
                ? message
                : message.Replace(value!, Tag(value!, piiType));

        private static string ProtectUserContent(string value)
            => Tag(value, WorkspaceContentPiiType);

        private static string Tag(string value, string piiType)
            => $"<pii type=\"{piiType}\" encoded=\"true\">{value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")}</pii>";

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
