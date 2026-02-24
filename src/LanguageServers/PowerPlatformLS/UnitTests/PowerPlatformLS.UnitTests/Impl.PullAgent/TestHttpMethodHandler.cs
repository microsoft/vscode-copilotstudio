namespace Microsoft.PowerPlatformLS.UnitTests.Impl.PullAgent
{
    using Microsoft.Agents.ObjectModel;
    using System.Collections.Generic;
    using System.Net;
    using System.Net.Http;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;

    internal class TestHttpMethodHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            string content;
            if (request.RequestUri?.AbsoluteUri.EndsWith("content/botcomponents") == true)
            {
                content = JsonSerializer.Serialize(new PvaComponentChangeSet([], null, $"{nameof(TestHttpMethodHandler)} change token"));
            }
            else
            {
                content = "{\"message\": \"Success\"}";
            }

            return Task.FromResult(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(content),
            });
        }
    }
}