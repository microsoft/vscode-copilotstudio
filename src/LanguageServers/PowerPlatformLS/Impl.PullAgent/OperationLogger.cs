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
    /// Logs SDK operation timings at Trace level with consistent format.
    /// Failures are always logged at Error level.
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
            _logger.LogInformation("[Req: {ReqId}] Sync operation started: {Operation}", reqId, operation);
            Stopwatch stopwatch = Stopwatch.StartNew();
            try
            {
                T result = function();
                _logger.LogInformation("[Req: {ReqId}] Sync operation completed: {Operation}, duration={Duration}ms", reqId, operation, stopwatch.ElapsedMilliseconds);
                return result;
            }
            catch(Exception ex)
            {
                _logger.LogError(ex, "[Req: {ReqId}] Sync operation failed: {Operation}, duration={Duration}ms", reqId, operation, stopwatch.ElapsedMilliseconds);
                throw;
            }
        }

        public T Execute<T>(string activity, Func<T> function, IEnumerable<KeyValuePair<string, string>> dimensions)
        {
            int reqId = LspRequestContext.CurrentRequestId;
            _logger.LogInformation("[Req: {ReqId}] Sync operation started: {Activity}", reqId, activity);
            Stopwatch stopwatch = Stopwatch.StartNew();
            try
            {
                T result = function();
                _logger.LogInformation("[Req: {ReqId}] Sync operation completed: {Activity}, duration={Duration}ms", reqId, activity, stopwatch.ElapsedMilliseconds);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Req: {ReqId}] Sync operation failed: {Activity}, duration={Duration}ms", reqId, activity, stopwatch.ElapsedMilliseconds);
                throw;
            }
        }

        public async Task<T> ExecuteAsync<T>(string activity, Func<Task<T>> function)
        {
            int reqId = LspRequestContext.CurrentRequestId;
            _logger.LogInformation("[Req: {ReqId}] Sync operation started: {Activity}", reqId, activity);
            Stopwatch stopwatch = Stopwatch.StartNew();
            try
            {
                T result = await function();
                _logger.LogInformation("[Req: {ReqId}] Sync operation completed: {Activity}, duration={Duration}ms", reqId, activity, stopwatch.ElapsedMilliseconds);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Req: {ReqId}] Sync operation failed: {Activity}, duration={Duration}ms", reqId, activity, stopwatch.ElapsedMilliseconds);
                throw;
            }
        }

        public async Task<T> ExecuteAsync<T>(string activity, Func<Task<T>> function, IEnumerable<KeyValuePair<string, string>> dimensions)
        {
            int reqId = LspRequestContext.CurrentRequestId;
            _logger.LogInformation("[Req: {ReqId}] Sync operation started: {Activity}", reqId, activity);
            Stopwatch stopwatch = Stopwatch.StartNew();
            try
            {
                T result = await function();
                _logger.LogInformation("[Req: {ReqId}] Sync operation completed: {Activity}, duration={Duration}ms", reqId, activity, stopwatch.ElapsedMilliseconds);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Req: {ReqId}] Sync operation failed: {Activity}, duration={Duration}ms", reqId, activity, stopwatch.ElapsedMilliseconds);
                throw;
            }
        }
    }
}
