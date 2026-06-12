
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

        // Maps method name → stack of sequential IDs for correlation between Start/End.
        private static readonly ConcurrentDictionary<string, ConcurrentStack<int>> _activeIds = new();

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
        /// Allocates and returns the next sequential ID for a custom LSP method.
        /// Called by JsonRpcStream for Received/Response logs so all LSP logs share the same ID.
        /// </summary>
        internal static int AllocateRequestId(string method)
        {
            int id = Interlocked.Increment(ref _lspRequestCounter);
            var stack = _activeIds.GetOrAdd(method, _ => new ConcurrentStack<int>());
            stack.Push(id);
            return id;
        }

        /// <summary>
        /// Returns the current active ID for a method without removing it.
        /// Used by JsonRpcStream for Response logs.
        /// </summary>
        internal static int PeekRequestId(string method)
        {
            if (_activeIds.TryGetValue(method, out var stack) && stack.TryPeek(out var id))
            {
                return id;
            }
            return 0;
        }

        public void LogStartContext(string message, params object[] @params)
        {
            if (IsBuiltInLspMethod(message))
            {
                return;
            }

            // Peek at the ID allocated by JsonRpcStream (don't pop — EndContext needs it)
            int id = 0;
            if (_activeIds.TryGetValue(message, out var stack) && stack.TryPeek(out var peekId))
            {
                id = peekId;
            }

            _logger.LogTrace("#{Id} Starting {Method}", id, message);
        }

        public void LogEndContext(string message, params object[] @params)
        {
            // message format from QueueItem: "methodName, duration=Xms"
            var methodName = ExtractMethodName(message);
            if (IsBuiltInLspMethod(methodName))
            {
                return;
            }

            int id = 0;
            if (_activeIds.TryGetValue(methodName, out var stack))
            {
                stack.TryPop(out id);
            }

            var durationMs = ExtractDurationMs(message);
            _logger.LogTrace("#{Id} Completed {Method} ({Duration}ms)", id, methodName, durationMs);
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
            _logger.LogError(message, @params);
        }

        public void LogException(Exception exception, string? message = null, params object[] @params)
        {
            _logger.LogError(exception, message, @params);
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
            _logger.LogWarning(message, @params);
        }
    }
}
