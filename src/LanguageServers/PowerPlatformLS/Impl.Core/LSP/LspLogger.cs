
namespace Microsoft.PowerPlatformLS.Impl.Core.Lsp
{
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.Extensions.Logging;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using System;
    using System.Threading;

    internal class LspLogger : ILspLogger
    {
        private readonly ILogger<LspLogger> _logger;
        private readonly bool _isTestLogger;

        // Sequential counter for custom LSP request correlation.
        private static int _lspRequestCounter;

        public LspLogger(ILogger<LspLogger> logger, BuildVersionInfo? gitInfo = null, SessionInformation? sessionInformation = null)
        {
            if (gitInfo != null)
            {
                // This will add as a custom dimension to all logs. 
                logger.BeginScope(new Dictionary<string, object>
                {
                    ["GitHash"] = gitInfo.Hash ?? string.Empty,
                    ["Vsix"] = gitInfo.VsixVersion ?? string.Empty,
                    ["pid"] = Environment.ProcessId,
                    ["SessionId"] = sessionInformation?.SessionId ?? string.Empty
                });
            }

            _logger = logger;
            _isTestLogger = _logger.GetType().AssemblyQualifiedName?.StartsWith("Microsoft.PowerPlatformLS.UnitTests") == true;
        }

        /// <summary>
        /// Allocates the next sequential ID for a custom LSP method.
        /// Called by JsonRpcStream on receive; the ID flows through the
        /// AsyncLocal → ExecuteAsync → QueueItem.RequestId → SetCurrentRequestId.
        /// </summary>
        internal static int AllocateRequestId()
        {
            return Interlocked.Increment(ref _lspRequestCounter);
        }

        public void LogStartContext(string methodName, string? agentName = null)
        {
            if (IsBuiltInLspMethod(methodName))
            {
                return;
            }

            int reqId = LspRequestContext.CurrentRequestId;
            string agentSuffix = agentName != null ? $", agent=\"{agentName}\"" : "";

            _logger.LogInformation("[Req: {ReqId}] Started handler for: {Method}{Agent}", reqId, methodName, agentSuffix);
        }

        public void LogEndContext(string methodName, long durationMs = -1, HandlerOutcome outcome = HandlerOutcome.Success, string? agentName = null)
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
            string agentSuffix = agentName != null ? $", agent=\"{agentName}\"" : "";
            if (durationMs >= 0)
            {
                _logger.LogInformation("[Req: {ReqId}] {Outcome} handler for: {Method}{Agent}, duration={Duration}ms", reqId, outcomeText, methodName, agentSuffix, durationMs);
            }
            else
            {
                _logger.LogInformation("[Req: {ReqId}] {Outcome} handler for: {Method}{Agent}", reqId, outcomeText, methodName, agentSuffix);
            }
        }

        internal static bool IsBuiltInLspMethod(string message)
        {
            return message.StartsWith("textDocument/", StringComparison.Ordinal)
                || message.StartsWith("$/", StringComparison.Ordinal)
                || message.StartsWith("initialize", StringComparison.Ordinal)
                || message.StartsWith("shutdown", StringComparison.Ordinal)
                || message.StartsWith("exit", StringComparison.Ordinal)
                || message.StartsWith("workspace/didChange", StringComparison.Ordinal)
                || message.StartsWith("workspace/didRename", StringComparison.Ordinal);
        }

        public void LogError(string message, params object[] @params)
        {
            int reqId = LspRequestContext.CurrentRequestId;
            if (reqId > 0)
            {
                _logger.LogError("[Req: {ReqId}] {Message}", reqId, message);
            }
            else
            {
                _logger.LogError(message, @params);
            }
        }

        public void LogException(Exception exception, string? message = null, params object[] @params)
        {
            int reqId = LspRequestContext.CurrentRequestId;
            if (reqId > 0)
            {
                _logger.LogError(exception, "[Req: {ReqId}] {Message}", reqId, message ?? exception.Message);
            }
            else
            {
                _logger.LogError(exception, message ?? exception.Message, @params);
            }
        }

        public void LogDebug(string message, params object[] @params)
        {
            _logger.LogDebug(message, @params);
        }

        public void LogTrace(string message, params object[] @params)
        {
            _logger.LogTrace(message, @params);
        }

        public void SetCurrentRequestId(int requestId)
        {
            LspRequestContext.CurrentRequestId = requestId;
        }

        public void LogInformation(string message, params object[] @params)
        {
            _logger.LogInformation(message, @params);
        }

        public void LogSensitiveInformation(string message, string? altSafeMsg = null)
        {
#if DEBUG
            _logger.LogInformation(message);
#else
            if (_isTestLogger)
            {
                _logger.LogInformation(message);
            }
            else if (altSafeMsg != null)
            {
                _logger.LogInformation(altSafeMsg);
            }
#endif
        }

        public void LogSensitiveWarning(string message, string safeMessage)
        {
            int reqId = LspRequestContext.CurrentRequestId;
            string prefix = reqId > 0 ? $"[Req: {reqId}] " : "";
#if DEBUG
            _logger.LogWarning("{Prefix}{Message}", prefix, message);
#else
            if (_isTestLogger)
            {
                _logger.LogWarning("{Prefix}{Message}", prefix, message);
            }
            else
            {
                // Safe message to telemetry (Warning level — reaches App Insights).
                // Full message is visible in the LSP response trace log.
                _logger.LogWarning("{Prefix}{Message}", prefix, safeMessage);
            }
#endif
        }

        public void LogSensitiveError(string message, string safeMessage)
        {
            int reqId = LspRequestContext.CurrentRequestId;
            string prefix = reqId > 0 ? $"[Req: {reqId}] " : "";
#if DEBUG
            _logger.LogError("{Prefix}{Message}", prefix, message);
#else
            if (_isTestLogger)
            {
                _logger.LogError("{Prefix}{Message}", prefix, message);
            }
            else
            {
                // Safe message to telemetry (Error level — reaches App Insights).
                // Full message is visible in the LSP response trace log.
                _logger.LogError("{Prefix}{Message}", prefix, safeMessage);
            }
#endif
        }

        public void LogWarning(string message, params object[] @params)
        {
            int reqId = LspRequestContext.CurrentRequestId;
            if (reqId > 0)
            {
                _logger.LogWarning("[Req: {ReqId}] {Message}", reqId, message);
            }
            else
            {
                _logger.LogWarning(message, @params);
            }
        }
    }
}
