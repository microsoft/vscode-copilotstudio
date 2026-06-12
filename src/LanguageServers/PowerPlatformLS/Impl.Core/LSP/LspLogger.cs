
namespace Microsoft.PowerPlatformLS.Impl.Core.Lsp
{
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.Extensions.Logging;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using System;
    using System.Collections.Concurrent;
    using System.Threading;

    internal class LspLogger : ILspLogger
    {
        private readonly ILogger<LspLogger> _logger;
        private readonly bool _isTestLogger;

        // Sequential counter for custom LSP methods only — no gaps in output.
        private static int _lspRequestCounter;

        // Stores allocated IDs keyed by method name. LogStartContext picks them up
        // and sets the AsyncLocal so downstream services (HTTP, Sync) can read it.
        private static readonly ConcurrentDictionary<string, ConcurrentStack<int>> _pendingIds = new();

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
        /// Allocates the next sequential ID for a custom LSP method and stores it
        /// for LogStartContext to pick up. Called by JsonRpcStream on receive.
        /// </summary>
        internal static int AllocateRequestId(string method)
        {
            int id = Interlocked.Increment(ref _lspRequestCounter);
            var stack = _pendingIds.GetOrAdd(method, _ => new ConcurrentStack<int>());
            stack.Push(id);
            return id;
        }

        public void LogStartContext(string message, params object[] @params)
        {
            if (IsBuiltInLspMethod(message))
            {
                return;
            }

            // Pop the ID allocated by JsonRpcStream and set it on the AsyncLocal
            // so downstream services (HTTP, Sync) in this handler's async flow can read it.
            int reqId = 0;
            if (_pendingIds.TryGetValue(message, out var stack))
            {
                stack.TryPop(out reqId);
            }
            LspRequestContext.CurrentRequestId = reqId;

            _logger.LogInformation("[Req: {ReqId}] Started handler for: {Method}", reqId, message);
        }

        public void LogEndContext(string message, params object[] @params)
        {
            // message format from QueueItem: "methodName, duration=Xms"
            var methodName = ExtractMethodName(message);
            if (IsBuiltInLspMethod(methodName))
            {
                return;
            }

            int reqId = LspRequestContext.CurrentRequestId;
            var durationMs = ExtractDurationMs(message);
            _logger.LogInformation("[Req: {ReqId}] Completed handler for: {Method}, duration={Duration}ms", reqId, methodName, durationMs);
        }

        private static string ExtractMethodName(string message)
        {
            var commaIndex = message.IndexOf(',');
            return commaIndex > 0 ? message[..commaIndex] : message;
        }

        private static string ExtractDurationMs(string message)
        {
            // Extract just the numeric value from "duration=Xms"
            var durationIndex = message.IndexOf("duration=", StringComparison.Ordinal);
            if (durationIndex < 0) return "?";
            var start = durationIndex + "duration=".Length;
            var end = message.IndexOf("ms", start, StringComparison.Ordinal);
            return end > start ? message[start..end] : "?";
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
                _logger.LogError($"[Req: {{ReqId}}] {message}", reqId);
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
                _logger.LogError(exception, $"[Req: {{ReqId}}] {message ?? exception.Message}", reqId);
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

        public void LogWarning(string message, params object[] @params)
        {
            int reqId = LspRequestContext.CurrentRequestId;
            if (reqId > 0)
            {
                _logger.LogWarning($"[Req: {{ReqId}}] {message}", reqId);
            }
            else
            {
                _logger.LogWarning(message, @params);
            }
        }
    }
}
