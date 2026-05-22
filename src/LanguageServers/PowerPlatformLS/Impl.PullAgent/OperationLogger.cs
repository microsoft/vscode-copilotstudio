namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.Agents.ObjectModel.Telemetry;
    using Microsoft.Extensions.Logging;
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Threading.Tasks;

    // Log from a IOperationLogger to MEL ILogger.
    internal class LspOperationLogger : IOperationLogger
    {
        private readonly ILogger<LspOperationLogger> _logger;

        public LspOperationLogger(ILogger<LspOperationLogger> logger)
        {
            _logger = logger;
        }

        public T Execute<T>(string operation, Func<T> function)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            try
            {
                T result = function();

                var ms = stopwatch.ElapsedMilliseconds;
                _logger.LogTrace("Operation: {Operation}, duration={DurationMs}ms", operation, ms);
                return result;
            }
            catch(Exception ex)
            {
                _logger.LogError(ex, "Operation failed: {Operation}", operation);
                throw;
            }
        }

        public T Execute<T>(string activity, Func<T> function, IEnumerable<KeyValuePair<string, string>> dimensions)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            try
            {
                T result = function();

                var ms = stopwatch.ElapsedMilliseconds;
                _logger.LogTrace("Activity: {Activity}, duration={DurationMs}ms", activity, ms);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Activity failed: {Activity}", activity);
                throw;
            }
        }

        public async Task<T> ExecuteAsync<T>(string activity, Func<Task<T>> function)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            try
            {
                T result = await function();

                var ms = stopwatch.ElapsedMilliseconds;
                _logger.LogTrace("Activity: {Activity}, duration={DurationMs}ms", activity, ms);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Activity failed: {Activity}", activity);
                throw;
            }
        }

        public async Task<T> ExecuteAsync<T>(string activity, Func<Task<T>> function, IEnumerable<KeyValuePair<string, string>> dimensions)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            try
            {
                T result = await function();

                var ms = stopwatch.ElapsedMilliseconds;
                _logger.LogTrace("Activity: {Activity}, duration={DurationMs}ms", activity, ms);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Activity failed: {Activity}", activity);
                throw;
            }
        }
    }
}
