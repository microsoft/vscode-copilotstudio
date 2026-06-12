namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.Agents.ObjectModel.Telemetry;
    using Microsoft.Extensions.Logging;
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
        private static int _operationId;

        public LspOperationLogger(ILogger<LspOperationLogger> logger)
        {
            _logger = logger;
        }

        public T Execute<T>(string operation, Func<T> function)
        {
            int id = Interlocked.Increment(ref _operationId);
            _logger.LogTrace("#{Id} Starting {Operation}", id, operation);
            Stopwatch stopwatch = Stopwatch.StartNew();
            try
            {
                T result = function();
                _logger.LogTrace("#{Id} Completed {Operation} ({Duration}ms)", id, operation, stopwatch.ElapsedMilliseconds);
                return result;
            }
            catch(Exception ex)
            {
                _logger.LogError(ex, "#{Id} Failed {Operation} ({Duration}ms)", id, operation, stopwatch.ElapsedMilliseconds);
                throw;
            }
        }

        public T Execute<T>(string activity, Func<T> function, IEnumerable<KeyValuePair<string, string>> dimensions)
        {
            int id = Interlocked.Increment(ref _operationId);
            _logger.LogTrace("#{Id} Starting {Activity}", id, activity);
            Stopwatch stopwatch = Stopwatch.StartNew();
            try
            {
                T result = function();
                _logger.LogTrace("#{Id} Completed {Activity} ({Duration}ms)", id, activity, stopwatch.ElapsedMilliseconds);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "#{Id} Failed {Activity} ({Duration}ms)", id, activity, stopwatch.ElapsedMilliseconds);
                throw;
            }
        }

        public async Task<T> ExecuteAsync<T>(string activity, Func<Task<T>> function)
        {
            int id = Interlocked.Increment(ref _operationId);
            _logger.LogTrace("#{Id} Starting {Activity}", id, activity);
            Stopwatch stopwatch = Stopwatch.StartNew();
            try
            {
                T result = await function();
                _logger.LogTrace("#{Id} Completed {Activity} ({Duration}ms)", id, activity, stopwatch.ElapsedMilliseconds);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "#{Id} Failed {Activity} ({Duration}ms)", id, activity, stopwatch.ElapsedMilliseconds);
                throw;
            }
        }

        public async Task<T> ExecuteAsync<T>(string activity, Func<Task<T>> function, IEnumerable<KeyValuePair<string, string>> dimensions)
        {
            int id = Interlocked.Increment(ref _operationId);
            _logger.LogTrace("#{Id} Starting {Activity}", id, activity);
            Stopwatch stopwatch = Stopwatch.StartNew();
            try
            {
                T result = await function();
                _logger.LogTrace("#{Id} Completed {Activity} ({Duration}ms)", id, activity, stopwatch.ElapsedMilliseconds);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "#{Id} Failed {Activity} ({Duration}ms)", id, activity, stopwatch.ElapsedMilliseconds);
                throw;
            }
        }
    }
}
