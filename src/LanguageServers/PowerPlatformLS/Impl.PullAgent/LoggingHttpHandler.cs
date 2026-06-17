namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.Extensions.Logging;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using System;
    using System.Diagnostics;
    using System.Threading.Tasks;

    /// <summary>
    /// Logs HTTP requests at Info level and network failures at Error level.
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
            int httpId = Interlocked.Increment(ref _requestId);
            int reqId = LspRequestContext.CurrentRequestId;
            var shortUri = GetPathAndQuery(request.RequestUri);

            _logger.LogInformation("[Req: {ReqId}] HTTP request #{HttpId} started: {Method} {Uri}", reqId, httpId, request.Method, shortUri);
            Stopwatch sw = Stopwatch.StartNew();

            try
            {
                var response = await base.SendAsync(request, cancellationToken);
                _logger.LogInformation("[Req: {ReqId}] HTTP response #{HttpId} completed: {Method} {Uri}, duration={Duration}ms, status={StatusCode}", reqId, httpId, request.Method, shortUri, sw.ElapsedMilliseconds, (int)response.StatusCode);
                return response;
            }
            catch (Exception e)
            {
                _logger.LogError("[Req: {ReqId}] HTTP request #{HttpId} failed: {Method} {Uri}, duration={Duration}ms, error={Message}", reqId, httpId, request.Method, shortUri, sw.ElapsedMilliseconds, e.Message);
                throw;
            }
        }

        private static string GetPathAndQuery(Uri? uri)
        {
            if (uri == null) return string.Empty;
            return uri.PathAndQuery;
        }
    }
}
