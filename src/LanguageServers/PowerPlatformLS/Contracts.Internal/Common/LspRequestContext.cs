namespace Microsoft.PowerPlatformLS.Contracts.Internal.Common
{
    using Microsoft.Extensions.Logging;
    using System;
    using System.Collections.Generic;
    using System.Threading;

    /// <summary>
    /// Provides ambient context for the current LSP request.
    /// Set by JsonRpcStream at receive time, carried through the queue via
    /// QueueItem.RequestId, and restored by SetCurrentRequestId before handler execution.
    /// Read by downstream services (HTTP handler, operation logger) to correlate logs.
    ///
    /// Agent/environment context uses a mutable holder class so that modifications made
    /// inside handler async methods are visible to the caller (QueueItem) after the handler
    /// returns. AsyncLocal value-type writes in child async contexts don't propagate back
    /// to the parent, but mutations to a shared object reference do.
    /// </summary>
    public static class LspRequestContext
    {
        private static readonly AsyncLocal<int> _currentRequestId = new();
        private static readonly AsyncLocal<AgentContextData?> _agentContext = new();
        private static readonly AsyncLocal<long?> _pendingDuration = new();

        /// <summary>
        /// Per-log-call duration metadata. Set just before a log call so the output channel
        /// provider can append "duration=Xms". Telemetry gets it as a separate structured property.
        /// </summary>
        public static long? PendingDurationMs
        {
            get => _pendingDuration.Value;
            set => _pendingDuration.Value = value;
        }

        /// <summary>
        /// Returns a combined scope that sets <see cref="PendingDurationMs"/> (for output channel)
        /// and adds durationMs as a telemetry dimension via BeginScope (for App Insights).
        /// </summary>
        public static IDisposable WithDuration(long durationMs, ILogger logger)
        {
            var previousDuration = _pendingDuration.Value;
            _pendingDuration.Value = durationMs;
            var loggerScope = logger.BeginScope(new Dictionary<string, object> { [TelemetryDimensions.DurationMs] = durationMs });
            return new DurationScope(previousDuration, loggerScope);
        }

        private sealed class DurationScope : IDisposable
        {
            private readonly long? _previousDuration;
            private readonly IDisposable? _loggerScope;

            public DurationScope(long? previousDuration, IDisposable? loggerScope = null)
            {
                _previousDuration = previousDuration;
                _loggerScope = loggerScope;
            }

            public void Dispose()
            {
                _pendingDuration.Value = _previousDuration;
                _loggerScope?.Dispose();
            }
        }

        /// <summary>
        /// Gets or sets the current LSP request ID for the executing async flow.
        /// Returns 0 when no request is active.
        /// </summary>
        public static int CurrentRequestId
        {
            get => _currentRequestId.Value;
            set
            {
                _currentRequestId.Value = value;
                // Initialize a fresh mutable context holder for this request.
                // Because it's a reference type, handler mutations are visible to the caller.
                _agentContext.Value = new AgentContextData();
            }
        }

        /// <summary>Agent display name for the current request, or null if not available.</summary>
        public static string? AgentName
        {
            get => _agentContext.Value?.AgentName;
            set { EnsureContext().AgentName = value; }
        }

        /// <summary>Agent ID (GUID) for the current request, or null if not available.</summary>
        public static string? AgentId
        {
            get => _agentContext.Value?.AgentId;
            set { EnsureContext().AgentId = value; }
        }

        /// <summary>Environment display name for the current request, or null if not available.</summary>
        public static string? EnvironmentName
        {
            get => _agentContext.Value?.EnvironmentName;
            set { EnsureContext().EnvironmentName = value; }
        }

        /// <summary>Environment ID for the current request, or null if not available.</summary>
        public static string? EnvironmentId
        {
            get => _agentContext.Value?.EnvironmentId;
            set { EnsureContext().EnvironmentId = value; }
        }

        /// <summary>
        /// Sets all agent/environment context fields at once. Called by handlers that have this info.
        /// </summary>
        public static void SetAgentContext(string? agentName, string? agentId, string? environmentName, string? environmentId)
        {
            var ctx = EnsureContext();
            ctx.AgentName = agentName;
            ctx.AgentId = agentId;
            ctx.EnvironmentName = environmentName;
            ctx.EnvironmentId = environmentId;
        }

        private static AgentContextData EnsureContext()
        {
            var ctx = _agentContext.Value;
            if (ctx == null)
            {
                ctx = new AgentContextData();
                _agentContext.Value = ctx;
            }
            return ctx;
        }

        /// <summary>
        /// When true, the output channel logger provider will NOT append the
        /// agent/env suffix to output channel messages. Telemetry (App Insights) still gets
        /// the context as separate dimensions. Use <see cref="SuppressOutputContext"/> for a
        /// scoped suppression.
        /// </summary>
        public static bool IsOutputContextSuppressed => _agentContext.Value?.SuppressOutputContext ?? false;

        /// <summary>
        /// Returns a disposable that suppresses agent/env context in the output channel
        /// for the duration of the scope. Telemetry dimensions are unaffected.
        /// </summary>
        public static IDisposable SuppressOutputContext()
        {
            var ctx = EnsureContext();
            var previous = ctx.SuppressOutputContext;
            ctx.SuppressOutputContext = true;
            return new OutputContextSuppression(ctx, previous);
        }

        private sealed class OutputContextSuppression : IDisposable
        {
            private readonly AgentContextData _ctx;
            private readonly bool _previous;
            public OutputContextSuppression(AgentContextData ctx, bool previous) { _ctx = ctx; _previous = previous; }
            public void Dispose() => _ctx.SuppressOutputContext = _previous;
        }

        /// <summary>
        /// Mutable holder for agent/environment context. Stored as a reference in AsyncLocal
        /// so that handler mutations propagate to the caller's execution context.
        /// </summary>
        internal sealed class AgentContextData
        {
            public string? AgentName;
            public string? AgentId;
            public string? EnvironmentName;
            public string? EnvironmentId;
            public bool SuppressOutputContext;
        }
    }
}
