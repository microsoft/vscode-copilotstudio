namespace Microsoft.PowerPlatformLS.Impl.Core.Lsp
{
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.Extensions.Logging;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using System;
    using System.Collections.Generic;
    using System.Threading;

    internal class LspLogger : ILspLogger
    {
        private static int _lspRequestCounter;
        private readonly ILogger<LspLogger> _logger;
        private readonly bool _isTestLogger;

        public LspLogger(ILogger<LspLogger> logger, BuildVersionInfo? gitInfo = null, SessionInformation? sessionInformation = null)
        {
            if (gitInfo != null)
            {
                logger.BeginScope(new Dictionary<string, object>
                {
                    [TelemetryDimensions.GitHash] = gitInfo.Hash ?? string.Empty,
                    [TelemetryDimensions.Version] = gitInfo.VsixVersion ?? string.Empty,
                    [TelemetryDimensions.Pid] = Environment.ProcessId,
                    [TelemetryDimensions.SessionId] = sessionInformation?.SessionId ?? string.Empty,
                    [TelemetryDimensions.IsDevMode] = (sessionInformation?.IsDevMode ?? false).ToString().ToLowerInvariant()
                });
            }

            _logger = logger;
            _isTestLogger = _logger.GetType().AssemblyQualifiedName?.StartsWith("Microsoft.PowerPlatformLS.UnitTests") == true;
        }

        // -------------------------------------------------------------------
        // Request lifecycle
        // -------------------------------------------------------------------

        public void SetCurrentRequestId(int requestId)
        {
            LspRequestContext.CurrentRequestId = requestId;
        }

        public void LogStartContext(string methodName)
        {
            if (IsBuiltInLspMethod(methodName))
            {
                return;
            }

            int reqId = LspRequestContext.CurrentRequestId;
            _logger.LogInformation("[Req: {reqId}] Started handler for: {lspMethod}", reqId, methodName);
        }

        public void LogEndContext(string methodName, long durationMs = -1, HandlerOutcome outcome = HandlerOutcome.Success)
        {
            if (IsBuiltInLspMethod(methodName))
            {
                return;
            }

            int reqId = LspRequestContext.CurrentRequestId;
            string outcomeText = outcome switch
            {
                HandlerOutcome.Canceled => "Canceled",
                HandlerOutcome.Failure => "Failed",
                _ => "Completed",
            };

            if (durationMs >= 0)
            {
                using (LspRequestContext.WithDuration(durationMs, _logger))
                {
                    LogAtOutcomeLevel(outcome, "[Req: {reqId}] {outcome} handler for: {lspMethod}",
                        reqId, outcomeText, methodName);
                }
            }
            else
            {
                LogAtOutcomeLevel(outcome, "[Req: {reqId}] {outcome} handler for: {lspMethod}",
                    reqId, outcomeText, methodName);
            }
        }

        // -------------------------------------------------------------------
        // Standard logging
        // -------------------------------------------------------------------

        public void LogTrace(string message, params object[] @params)
        {
            _logger.LogTrace(message, @params);
        }

        public void LogDebug(string message, params object[] @params)
        {
            _logger.LogDebug(message, @params);
        }

        public void LogInformation(string message, params object[] @params)
        {
            _logger.LogInformation(message, @params);
        }

        // -------------------------------------------------------------------
        // Warning/Error logging (with [Req: X] prefix for output channel)
        // reqId dimension is handled globally by PiiScrubTelemetryInitializer.
        // -------------------------------------------------------------------

        public void LogWarning(string message, params object[] @params)
        {
            _logger.LogWarning(GetReqPrefix() + message, @params);
        }

        public void LogError(string message, params object[] @params)
        {
            _logger.LogError(GetReqPrefix() + message, @params);
        }

        public void LogException(Exception exception, string? message = null, params object[] @params)
        {
            _logger.LogError(exception, GetReqPrefix() + (message ?? string.Empty), @params);
        }

        // -------------------------------------------------------------------
        // Sensitive logging (full message in debug/test, safe message in prod)
        // -------------------------------------------------------------------

        public void LogSensitiveInformation(string message, string safeMessage)
        {
#if DEBUG
            _logger.LogInformation(message);
#else
            _logger.LogInformation(_isTestLogger ? message : safeMessage);
#endif
        }

        public void LogSensitiveWarning(string message, string safeMessage)
        {
            string prefix = GetReqPrefix();
#if DEBUG
            _logger.LogWarning(prefix + message);
#else
            _logger.LogWarning(prefix + (_isTestLogger ? message : safeMessage));
#endif
        }

        public void LogSensitiveError(string message, string safeMessage)
        {
            string prefix = GetReqPrefix();
#if DEBUG
            _logger.LogError(prefix + message);
#else
            _logger.LogError(prefix + (_isTestLogger ? message : safeMessage));
#endif
        }

        // -------------------------------------------------------------------
        // Internal/Private helpers
        // -------------------------------------------------------------------

        /// <summary>
        /// Allocates the next sequential ID for a custom LSP method.
        /// Called by JsonRpcStream on receive; the ID flows through the
        /// AsyncLocal -> ExecuteAsync -> QueueItem.RequestId -> SetCurrentRequestId.
        /// </summary>
        internal static int AllocateRequestId()
        {
            return Interlocked.Increment(ref _lspRequestCounter);
        }

        internal static bool IsBuiltInLspMethod(string method)
        {
            return method.StartsWith("textDocument/", StringComparison.Ordinal)
                || method.StartsWith("$/", StringComparison.Ordinal)
                || method.StartsWith("initialize", StringComparison.Ordinal)
                || method.StartsWith("shutdown", StringComparison.Ordinal)
                || method.StartsWith("exit", StringComparison.Ordinal)
                || method.StartsWith("workspace/didChange", StringComparison.Ordinal)
                || method.StartsWith("workspace/didRename", StringComparison.Ordinal);
        }

        private static string GetReqPrefix()
        {
            int reqId = LspRequestContext.CurrentRequestId;
            return reqId > 0 ? $"[Req: {reqId}] " : "";
        }

        private void LogAtOutcomeLevel(HandlerOutcome outcome, string message, params object[] args)
        {
            switch (outcome)
            {
                case HandlerOutcome.Failure:
                    _logger.LogError(message, args);
                    break;
                case HandlerOutcome.Canceled:
                    _logger.LogWarning(message, args);
                    break;
                default:
                    _logger.LogInformation(message, args);
                    break;
            }
        }
    }
}