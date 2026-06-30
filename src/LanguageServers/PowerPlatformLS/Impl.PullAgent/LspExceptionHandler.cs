namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.Agents.Platform.Content.Exceptions;
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using System;
    using System.Net;
    using System.Net.Http;
    using System.Threading;

    /// <summary>
    /// Classifies exceptions and logs them at the appropriate level.
    /// Provides consistent error handling across all LSP request handlers.
    /// Each error includes [ExceptionType] and source location (at File.cs:Line) for traceability.
    /// Exceptions already logged by instrumented layers (HTTP, Sync) are not duplicated.
    /// </summary>
    internal static class LspExceptionHandler
    {
        /// <summary>
        /// Classifies the exception, logs it at the appropriate severity, and returns
        /// a status code and user-facing message suitable for the LSP response.
        /// </summary>
        public static (int Code, string Message) Handle(Exception ex, ILspLogger logger, CancellationToken cancellationToken = default)
        {
            return ex switch
            {
                // HTTP failures: already logged at Error by LoggingHttpHandler — don't duplicate.
                HttpRequestException hre =>
                    NoLog(MapHttpStatusCode(hre), hre.Message),

                // User-recoverable: the user can fix this by performing an action (resync, reclone, etc.).
                FileNotFoundException fnf =>
                    LogTypedError(logger, 400, fnf),

                DirectoryNotFoundException dnf =>
                    LogTypedError(logger, 400, dnf),

                // Connection binding failed during reattach (e.g., missing connector config).
                ConnectionBindingException cbe =>
                    LogTypedError(logger, 400, cbe),

                // User validation: caller explicitly threw to signal bad input.
                InvalidOperationException ioe =>
                    LogTypedError(logger, 400, ioe),

                // Service rejected the request (known Dataverse error with status code).
                // Message may contain customer data — use safe telemetry message.
                DataverseBadRequestException dbre =>
                    LogTypedSensitiveError(logger, dbre.StatusCode, dbre, $"Dataverse request failed with status {dbre.StatusCode}."),

                // Service temporarily unavailable.
                DataverseServiceUnavailableException dsue =>
                    LogTypedError(logger, 503, dsue,
                        "The Copilot Studio service is temporarily unavailable. Please try again in a moment."),

                // Cancellation: distinguish user-initiated from timeout.
                OperationCanceledException when cancellationToken.IsCancellationRequested =>
                    NoLog(499, "Operation was cancelled."),

                OperationCanceledException =>
                    NoLog(504, "Operation timed out."),

                // Unexpected: genuine bugs or unhandled conditions.
                // Log at Error WITH full stack trace for diagnostics.
                _ => LogErrorWithTrace(logger, ex),
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
        /// Logs error with [ExceptionType], message, and source location.
        /// </summary>
        private static (int Code, string Message) LogTypedError(ILspLogger logger, int code, Exception ex)
        {
            string message = ex.Message;
            string typeName = ex.GetType().Name;
            var source = ExceptionSourceExtractor.FormatSource(ex);
            logger.LogError($"[{typeName}] {message}{source}");
            return (code, message);
        }

        /// <summary>
        /// Logs error with explicit override message (for cases where the exception message isn't user-facing).
        /// </summary>
        private static (int Code, string Message) LogTypedError(ILspLogger logger, int code, Exception ex, string displayMessage)
        {
            string typeName = ex.GetType().Name;
            var source = ExceptionSourceExtractor.FormatSource(ex);
            logger.LogError($"[{typeName}] {displayMessage}{source}");
            return (code, displayMessage);
        }

        /// <summary>
        /// Logs error with [ExceptionType] and source. The exception message may contain customer data,
        /// so the safe message goes to telemetry and the full message stays in the output channel.
        /// </summary>
        private static (int Code, string Message) LogTypedSensitiveError(ILspLogger logger, int code, Exception ex, string safeMessage)
        {
            string message = ex.Message;
            string typeName = ex.GetType().Name;
            var source = ExceptionSourceExtractor.FormatSource(ex);
            logger.LogSensitiveError($"[{typeName}] {message}{source}", $"[{typeName}] {safeMessage}{source}");
            return (code, message);
        }

        private static (int Code, string Message) LogErrorWithTrace(ILspLogger logger, Exception ex)
        {
            string typeName = ex.GetType().Name;
            var source = ExceptionSourceExtractor.FormatSource(ex);
            logger.LogException(ex, $"[{typeName}] {ex.Message}{source}");
            return (500, ex.Message);
        }

        private static int MapHttpStatusCode(HttpRequestException hre)
        {
            if (hre.StatusCode.HasValue)
            {
                return (int)hre.StatusCode.Value switch
                {
                    401 or 403 => 401,
                    429 => 429,
                    >= 500 => 502,
                    _ => 502,
                };
            }

            return 502;
        }
    }
}
