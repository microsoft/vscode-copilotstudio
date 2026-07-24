namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.Agents.ObjectModel.Telemetry;
    using Microsoft.Extensions.Logging;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Logs SDK operation timings with consistent format.
    /// Failures are logged at Error level with exception message and source location.
    /// Agent/environment context is NOT shown in the output channel (suppressed to reduce noise),
    /// but is still sent as telemetry dimensions by the PiiScrubTelemetryInitializer.
    /// </summary>
    internal class LspOperationLogger : IOperationLogger
    {
        private readonly ILogger<LspOperationLogger> _logger;

        public LspOperationLogger(ILogger<LspOperationLogger> logger)
        {
            _logger = logger;
        }

        public T Execute<T>(string operation, Func<T> function)
        {
            int reqId = LspRequestContext.CurrentRequestId;
            LogStarted(reqId, operation);
            Stopwatch stopwatch = Stopwatch.StartNew();
            try
            {
                T result = function();
                LogCompleted(reqId, operation, stopwatch.ElapsedMilliseconds);
                return result;
            }
            catch(Exception ex)
            {
                LogFailed(ex, reqId, operation, stopwatch.ElapsedMilliseconds);
                throw;
            }
        }

        public T Execute<T>(string activity, Func<T> function, IEnumerable<KeyValuePair<string, string>> dimensions)
        {
            int reqId = LspRequestContext.CurrentRequestId;
            LogStarted(reqId, activity);
            Stopwatch stopwatch = Stopwatch.StartNew();
            try
            {
                T result = function();
                LogCompleted(reqId, activity, stopwatch.ElapsedMilliseconds);
                return result;
            }
            catch (Exception ex)
            {
                LogFailed(ex, reqId, activity, stopwatch.ElapsedMilliseconds);
                throw;
            }
        }

        public async Task<T> ExecuteAsync<T>(string activity, Func<Task<T>> function)
        {
            int reqId = LspRequestContext.CurrentRequestId;
            LogStarted(reqId, activity);
            Stopwatch stopwatch = Stopwatch.StartNew();
            try
            {
                T result = await function();
                LogCompleted(reqId, activity, stopwatch.ElapsedMilliseconds);
                return result;
            }
            catch (Exception ex)
            {
                LogFailed(ex, reqId, activity, stopwatch.ElapsedMilliseconds);
                throw;
            }
        }

        public async Task<T> ExecuteAsync<T>(string activity, Func<Task<T>> function, IEnumerable<KeyValuePair<string, string>> dimensions)
        {
            int reqId = LspRequestContext.CurrentRequestId;
            LogStarted(reqId, activity);
            Stopwatch stopwatch = Stopwatch.StartNew();
            try
            {
                T result = await function();
                LogCompleted(reqId, activity, stopwatch.ElapsedMilliseconds);
                return result;
            }
            catch (Exception ex)
            {
                LogFailed(ex, reqId, activity, stopwatch.ElapsedMilliseconds);
                throw;
            }
        }

        private void LogStarted(int reqId, string activity)
        {
            using (LspRequestContext.SuppressOutputContext())
            {
                _logger.LogInformation("[Req: {reqId}] Sync operation started: {syncFunction}", reqId, activity);
            }
        }

        private void LogCompleted(int reqId, string activity, long durationMs)
        {
            using (LspRequestContext.SuppressOutputContext())
            using (LspRequestContext.WithDuration(durationMs, _logger))
            {
                _logger.LogInformation("[Req: {reqId}] Sync operation {outcome}: {syncFunction}", reqId, "completed", activity);
            }
        }

        private void LogFailed(Exception ex, int reqId, string activity, long durationMs)
        {
            using (LspRequestContext.SuppressOutputContext())
            using (LspRequestContext.WithDuration(durationMs, _logger))
            {
                _logger.LogError("[Req: {reqId}] Sync operation {outcome}: {syncFunction}. " + ex.Message,
                    reqId, "failed", activity);
            }
        }
    }
}
