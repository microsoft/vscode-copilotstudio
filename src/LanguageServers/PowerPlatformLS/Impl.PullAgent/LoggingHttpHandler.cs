namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using System;
    using System.Diagnostics;
    using System.Threading.Tasks;

    // Log Http requests to a ILspLogger.
    // Useful to find long-running requests, network failures, etc. 
    internal class LoggingHttpHandler : DelegatingHandler
    {
        private readonly ILspLogger _logger;

        public LoggingHttpHandler(ILspLogger logger)
        {
            _logger = logger;
        }

        // Random Id to correlate start and end messages. 
        private static int _randomId;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            int counter = Interlocked.Increment(ref _randomId);

            _logger.LogInformation($"HTTP: Start id={counter}, {request.Method} {request.RequestUri}");
            Stopwatch sw = Stopwatch.StartNew();

            try
            {
                var response = await base.SendAsync(request, cancellationToken);

                _logger.LogInformation($"HTTP: End id={counter}, result={response.StatusCode}, ms={sw.ElapsedMilliseconds},");

                return response;
            }
            catch (Exception e)
            {
                // This would be a network failure. 
                _logger.LogError($"HTTP: Exception, id={counter}, msg ={e.Message}, ms={sw.ElapsedMilliseconds},");
                throw;
            }
        }
    }
}
