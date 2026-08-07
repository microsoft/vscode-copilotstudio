namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.Extensions.Logging;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Text.RegularExpressions;
    using System.Threading.Tasks;

    /// <summary>
    /// Logs HTTP request lifecycle. Successful responses at Info, failures at Error.
    /// For non-2xx responses and network errors, this is the single authoritative error line —
    /// LspExceptionHandler will not duplicate it for HttpRequestException.
    /// Agent/environment context is NOT shown in the output channel (suppressed to reduce noise),
    /// but is still sent as telemetry dimensions by the PiiScrubTelemetryInitializer.
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
            var endpoint = GetPathAndQuery(request.RequestUri);

            using (LspRequestContext.SuppressOutputContext())
            {
                _logger.LogInformation($"[Req: {{reqId}}] HTTP request #{httpId} started: {{httpMethod}} {{httpEndpoint}}",
                    reqId, request.Method.ToString(), endpoint);
            }

            Stopwatch sw = Stopwatch.StartNew();

            try
            {
                var response = await base.SendAsync(request, cancellationToken);
                using (LspRequestContext.SuppressOutputContext())
                using (LspRequestContext.WithDuration(sw.ElapsedMilliseconds, _logger))
                {
                    if (response.IsSuccessStatusCode)
                    {
                        _logger.LogInformation($"[Req: {{reqId}}] HTTP request #{httpId} {{outcome}}: {{httpMethod}} {{httpEndpoint}}, status={{httpStatusCode}}",
                            reqId, "completed", request.Method.ToString(), endpoint, (int)response.StatusCode);
                    }
                    else
                    {
                        _logger.LogError($"[Req: {{reqId}}] HTTP request #{httpId} {{outcome}}: {{httpMethod}} {{httpEndpoint}}, status={{httpStatusCode}}, reason={{httpReason}}",
                            reqId, "failed", request.Method.ToString(), endpoint, (int)response.StatusCode, response.ReasonPhrase ?? "unknown");
                    }
                }
                return response;
            }
            catch (Exception ex)
            {
                using (LspRequestContext.SuppressOutputContext())
                using (LspRequestContext.WithDuration(sw.ElapsedMilliseconds, _logger))
                {
                    _logger.LogError($"[Req: {{reqId}}] HTTP request #{httpId} {{outcome}}: {{httpMethod}} {{httpEndpoint}}. " + ex.Message,
                        reqId, "failed", request.Method.ToString(), endpoint);
                }
                throw;
            }
        }

        /// <summary>
        /// Returns the path portion of the URI with GUIDs normalized to {id} for grouping in telemetry.
        /// Query strings are stripped (may contain tokens/PII).
        /// </summary>
        private static string GetPathAndQuery(Uri? uri)
        {
            if (uri == null) return string.Empty;
            return NormalizeGuids(uri.AbsolutePath);
        }

        private static readonly Regex GuidPattern = new(
            @"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Normalizes GUIDs in a URL path so endpoints group cleanly in telemetry.
        /// - Segments containing a GUID inside parentheses (OData keys like <c>workflows(guid)</c>)
        ///   keep the entity name: <c>workflows({id})</c>.
        /// - Segments that are entirely or partially a GUID (like <c>Default-guid</c> or bare <c>guid</c>)
        ///   are replaced with <c>{id}</c>.
        /// </summary>
        private static string NormalizeGuids(string path)
        {
            var segments = path.Split('/');
            for (int i = 0; i < segments.Length; i++)
            {
                if (!GuidPattern.IsMatch(segments[i]))
                {
                    continue;
                }

                if (segments[i].Contains('(') && segments[i].Contains(')'))
                {
                    // OData key notation: entity(guid) → entity({id})
                    segments[i] = GuidPattern.Replace(segments[i], "{id}");
                }
                else
                {
                    // Bare GUID or prefixed (e.g., Default-guid) → {id}
                    segments[i] = "{id}";
                }
            }
            return string.Join('/', segments);
        }
    }
}
