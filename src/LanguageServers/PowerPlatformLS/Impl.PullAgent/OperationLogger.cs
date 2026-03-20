namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.Agents.ObjectModel.Telemetry;
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Threading.Tasks;

    // Log from a IOperationLogger to a ILspLogger.
    internal class LspOperationLogger : IOperationLogger
    {
        private readonly ILspLogger _logger;

        public LspOperationLogger(ILspLogger logger)
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
                _logger.LogInformation($"Operation: {operation}, duration={ms}ms");
                return result;
            }
            catch(Exception ex)
            {
                _logger.LogException(ex, operation);
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
                _logger.LogInformation($"Activity: {activity}, duration={ms}ms");
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogException(ex, activity);
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
                _logger.LogInformation($"Activity: {activity}, duration={ms}ms");
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogException(ex, activity);
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
                _logger.LogInformation($"Activity: {activity}, duration={ms}ms");
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogException(ex, activity);
                throw;
            }
        }
    }
}
