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
    /// </summary>
    internal static class LspExceptionHandler
    {
        /// <summary>
        /// Classifies the exception, logs it at the appropriate severity, and returns
        /// a status code and user-facing message suitable for the LSP response.
        /// All failures are logged at Error level (matching the UI's failure display)
        /// but only unexpected exceptions include the full stack trace.
        /// </summary>
        /// <param name="ex">The caught exception.</param>
        /// <param name="logger">The LSP logger instance.</param>
        /// <param name="cancellationToken">The request's cancellation token, used to distinguish user cancellation from timeouts.</param>
        public static (int Code, string Message) Handle(Exception ex, ILspLogger logger, CancellationToken cancellationToken = default)
        {
            return ex switch
            {
                // User-recoverable: the user can fix this by performing an action (resync, reclone, etc.).
                // Log at Error (message only, no stack trace) to match failure indicators in the UI.
                FileNotFoundException fnf =>
                    LogErrorMessage(logger, 400, fnf.Message),

                DirectoryNotFoundException dnf =>
                    LogErrorMessage(logger, 400, dnf.Message),

                // Connection binding failed during reattach (e.g., missing connector config).
                ConnectionBindingException cbe =>
                    LogErrorMessage(logger, 400, cbe.Message),

                // User validation: caller explicitly threw to signal bad input.
                InvalidOperationException ioe =>
                    LogErrorMessage(logger, 400, ioe.Message),

                // Service rejected the request (known Dataverse error with status code).
                DataverseBadRequestException dbre =>
                    LogErrorMessage(logger, dbre.StatusCode, dbre.Message),

                // Service temporarily unavailable.
                DataverseServiceUnavailableException =>
                    LogErrorMessage(logger, 503, "The Copilot Studio service is temporarily unavailable. Please try again in a moment."),

                // Network/HTTP failures.
                HttpRequestException hre =>
                    LogErrorMessage(logger, MapHttpStatusCode(hre), $"Network error: {hre.Message}"),

                // Cancellation: distinguish user-initiated from timeout.
                OperationCanceledException when cancellationToken.IsCancellationRequested =>
                    LogErrorMessage(logger, 499, "Operation was cancelled."),

                OperationCanceledException oce =>
                    LogErrorMessage(logger, 504, $"Operation timed out: {oce.Message}"),

                // Unexpected: genuine bugs or unhandled conditions.
                // Log at Error WITH full stack trace for diagnostics.
                _ => LogErrorWithTrace(logger, ex),
            };
        }

        private static (int Code, string Message) LogErrorMessage(ILspLogger logger, int code, string message)
        {
            logger.LogError(message);
            return (code, message);
        }

        private static (int Code, string Message) LogErrorWithTrace(ILspLogger logger, Exception ex)
        {
            logger.LogException(ex);
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
