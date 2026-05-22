namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.Extensions.Logging;
    using System;
    using System.Diagnostics;
    using System.Threading.Tasks;

    // Log Http requests via MEL ILogger.
    // Useful to find long-running requests, network failures, etc. 
    internal class LoggingHttpHandler : DelegatingHandler
    {
        private readonly ILogger<LoggingHttpHandler> _logger;

        public LoggingHttpHandler(ILogger<LoggingHttpHandler> logger)
        {
            _logger = logger;
        }

        // Random Id to correlate start and end messages. 
        private static int _randomId;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            int counter = Interlocked.Increment(ref _randomId);

            _logger.LogTrace("HTTP: Start id={Counter}, {Method} {Uri}", counter, request.Method, request.RequestUri);
            Stopwatch sw = Stopwatch.StartNew();

            try
            {
                var response = await base.SendAsync(request, cancellationToken);

                _logger.LogTrace("HTTP: End id={Counter}, result={StatusCode}, ms={ElapsedMs}", counter, response.StatusCode, sw.ElapsedMilliseconds);

                return response;
            }
            catch (Exception e)
            {
                // This would be a network failure. 
                _logger.LogError("HTTP: Exception, id={Counter}, msg={Message}, ms={ElapsedMs}", counter, e.Message, sw.ElapsedMilliseconds);
                throw;
            }
        }
    }
}
