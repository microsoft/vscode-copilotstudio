namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.Extensions.Logging;
    using System;
    using System.Diagnostics;
    using System.Threading.Tasks;

    /// <summary>
    /// Logs HTTP requests at Trace level for diagnostics.
    /// Errors (network failures) are always logged at Error level.
    /// </summary>
    internal class LoggingHttpHandler : DelegatingHandler
    {
        private readonly ILogger<LoggingHttpHandler> _logger;

        public LoggingHttpHandler(ILogger<LoggingHttpHandler> logger)
        {
            _logger = logger;
        }

        private static int _requestId;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            int id = Interlocked.Increment(ref _requestId);

            _logger.LogTrace("#{Id} Starting {Method} {Uri}", id, request.Method, request.RequestUri);
            Stopwatch sw = Stopwatch.StartNew();

            try
            {
                var response = await base.SendAsync(request, cancellationToken);
                _logger.LogTrace("#{Id} Completed {Method} {Uri} ({Duration}ms): status={StatusCode}", id, request.Method, request.RequestUri, sw.ElapsedMilliseconds, (int)response.StatusCode);
                return response;
            }
            catch (Exception e)
            {
                _logger.LogError("#{Id} Failed {Method} {Uri} ({Duration}ms) - {Message}", id, request.Method, request.RequestUri, sw.ElapsedMilliseconds, e.Message);
                throw;
            }
        }
    }
}
