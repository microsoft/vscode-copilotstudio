namespace Microsoft.PowerPlatformLS.UnitTests.Impl.PullAgent
{
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using Microsoft.PowerPlatformLS.Impl.PullAgent;
    using Microsoft.PowerPlatformLS.UnitTests.TestUtilities;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Reflection;
    using System.Threading;
    using System.Threading.Tasks;
    using Xunit;

    [Collection("LoggingTestsCollection")]
    public class LoggingHttpHandlerTests : IDisposable
    {
        private readonly TestLogger _testLogger = new();

        public void Dispose()
        {
            LspRequestContext.CurrentRequestId = 0;
            ResetHttpRequestCounter();
        }

        [Fact]
        public async Task SendAsync_Logs_Request_Start_At_Information_Level()
        {
            using var response = new HttpResponseMessage(HttpStatusCode.OK);
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://contoso.crm.dynamics.com/api/data/v9.2/accounts?foo=bar");
            using var invoker = CreateInvoker((_, _) => Task.FromResult(response));

            using var result = await invoker.SendAsync(request, CancellationToken.None);

            var infoLogs = _testLogger.Info.ToList();
            Assert.Equal(2, infoLogs.Count);
            Assert.Contains("HTTP request #", infoLogs[0]);
            Assert.Contains("started: GET /api/data/v9.2/accounts?foo=bar", infoLogs[0]);
            Assert.DoesNotContain("contoso.crm.dynamics.com", infoLogs[0]);
        }

        [Fact]
        public async Task SendAsync_Logs_Response_Completion_With_StatusCode_And_Duration()
        {
            using var response = new HttpResponseMessage(HttpStatusCode.Accepted);
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://contoso.crm.dynamics.com/api/data/v9.2/accounts?foo=bar");
            using var invoker = CreateInvoker((_, _) => Task.FromResult(response));

            using var result = await invoker.SendAsync(request, CancellationToken.None);

            var completionLog = _testLogger.Info.ToList()[1];
            Assert.Contains("HTTP request #", completionLog);
            Assert.Contains("completed: POST /api/data/v9.2/accounts?foo=bar", completionLog);
            Assert.Contains("duration=", completionLog);
            Assert.Contains("status=202", completionLog);
        }

        [Fact]
        public async Task SendAsync_Logs_Error_On_Exception()
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, "https://contoso.crm.dynamics.com/api/data/v9.2/accounts(1)");
            using var invoker = CreateInvoker((_, _) => throw new HttpRequestException("network failure"));

            var exception = await Assert.ThrowsAsync<HttpRequestException>(() => invoker.SendAsync(request, CancellationToken.None));

            Assert.Equal("network failure", exception.Message);
            var errorLog = Assert.Single(_testLogger.Error);
            Assert.Contains("HTTP request #", errorLog);
            Assert.Contains("failed: DELETE /api/data/v9.2/accounts(1)", errorLog);
            Assert.Contains("duration=", errorLog);
            Assert.Contains("error=network failure", errorLog);
        }

        [Fact]
        public async Task SendAsync_Logs_Failed_For_Non_Success_StatusCode()
        {
            using var response = new HttpResponseMessage(HttpStatusCode.NotFound);
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://contoso.crm.dynamics.com/api/data/v9.2/bots(1)");
            using var invoker = CreateInvoker((_, _) => Task.FromResult(response));

            using var result = await invoker.SendAsync(request, CancellationToken.None);

            var errorLog = Assert.Single(_testLogger.Error);
            Assert.Contains("failed: GET /api/data/v9.2/bots(1)", errorLog);
            Assert.Contains("status=404", errorLog);
            Assert.DoesNotContain("completed", errorLog);
        }

        [Fact]
        public void GetPathAndQuery_Strips_Host_From_Full_Url()
        {
            var method = typeof(LoggingHttpHandler).GetMethod("GetPathAndQuery", BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(method);
            var result = (string)method!.Invoke(null, [new Uri("https://contoso.crm.dynamics.com/api/data/v9.2/accounts?foo=bar")])!;

            Assert.Equal("/api/data/v9.2/accounts?foo=bar", result);
        }

        [Fact]
        public void GetPathAndQuery_Returns_Empty_For_Null_Uri()
        {
            var method = typeof(LoggingHttpHandler).GetMethod("GetPathAndQuery", BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(method);
            var result = (string)method!.Invoke(null, [null])!;

            Assert.Equal(string.Empty, result);
        }

        [Fact]
        public async Task SendAsync_Includes_RequestId_From_LspRequestContext()
        {
            LspRequestContext.CurrentRequestId = 42;

            using var response = new HttpResponseMessage(HttpStatusCode.OK);
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://contoso.crm.dynamics.com/api/data/v9.2/accounts");
            using var invoker = CreateInvoker((_, _) => Task.FromResult(response));

            using var result = await invoker.SendAsync(request, CancellationToken.None);

            Assert.All(_testLogger.Info, log => Assert.Contains("[Req: 42]", log));
        }

        private HttpMessageInvoker CreateInvoker(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync)
        {
            var handler = new LoggingHttpHandler(new TestLogger<LoggingHttpHandler>(_testLogger))
            {
                InnerHandler = new StubHttpMessageHandler(sendAsync)
            };

            return new HttpMessageInvoker(handler);
        }

        private static void ResetHttpRequestCounter()
        {
            var field = typeof(LoggingHttpHandler).GetField("_requestId", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(field);
            field!.SetValue(null, 0);
        }

        private sealed class StubHttpMessageHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _sendAsync;

            public StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync)
            {
                _sendAsync = sendAsync;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return _sendAsync(request, cancellationToken);
            }
        }
    }

    [CollectionDefinition("LoggingTestsCollection", DisableParallelization = true)]
    public sealed class LoggingTestsCollection { }
}
