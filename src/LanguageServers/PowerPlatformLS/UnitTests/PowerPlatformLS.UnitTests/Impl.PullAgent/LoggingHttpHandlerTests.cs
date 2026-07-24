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
            Assert.Contains("started: GET /api/data/v9.2/accounts", infoLogs[0]);
            Assert.DoesNotContain("contoso.crm.dynamics.com", infoLogs[0]);
            Assert.DoesNotContain("foo=bar", infoLogs[0]);
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
            Assert.Contains("completed: POST /api/data/v9.2/accounts", completionLog);
            Assert.DoesNotContain("duration=", completionLog);
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
            Assert.DoesNotContain("duration=", errorLog);
            Assert.Contains("network failure", errorLog);
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

            Assert.Equal("/api/data/v9.2/accounts", result);
        }

        [Fact]
        public void GetPathAndQuery_Returns_Empty_For_Null_Uri()
        {
            var method = typeof(LoggingHttpHandler).GetMethod("GetPathAndQuery", BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(method);
            var result = (string)method!.Invoke(null, [null])!;

            Assert.Equal(string.Empty, result);
        }

        [Theory]
        [InlineData(
            "https://org.crm.dynamics.com/api/data/v9.2/workflows(3fa85f64-5717-4562-b3fc-2c963f66afa6)",
            "/api/data/v9.2/workflows({id})")]
        [InlineData(
            "https://org.crm.dynamics.com/api/data/v9.2/bots(3fa85f64-5717-4562-b3fc-2c963f66afa6)/bot_botcomponentcollection(7c9e1234-abcd-4000-8000-000000000000)/$ref",
            "/api/data/v9.2/bots({id})/bot_botcomponentcollection({id})/$ref")]
        [InlineData(
            "https://org.crm.dynamics.com/api/data/v9.2/botcomponents(AABBCCDD-1122-3344-5566-778899001122)/filedata/$value",
            "/api/data/v9.2/botcomponents({id})/filedata/$value")]
        [InlineData(
            "https://host.com/api/botmanagement/v1/environments/Default-c2983f0e-abc1-4def-9012-3456789abcde/bots/40b35f64-5717-4562-b3fc-2c963f66afa6/content/botcomponents",
            "/api/botmanagement/v1/environments/{id}/bots/{id}/content/botcomponents")]
        [InlineData(
            "https://host.com/chatbotmanagement/tenants/aabbccdd-1122-3344-5566-778899001122/environments/Default-c2983f0e-abc1-4def-9012-3456789abcde/componentcollections/api/11111111-2222-3333-4444-555555555555/get-content",
            "/chatbotmanagement/tenants/{id}/environments/{id}/componentcollections/api/{id}/get-content")]
        [InlineData(
            "https://org.crm.dynamics.com/api/data/v9.2/connectors",
            "/api/data/v9.2/connectors")]
        [InlineData(
            "https://org.crm.dynamics.com/api/data/v9.2/EntityDefinitions(LogicalName='connectionreference')/ManyToOneRelationships",
            "/api/data/v9.2/EntityDefinitions(LogicalName='connectionreference')/ManyToOneRelationships")]
        public void GetPathAndQuery_Normalizes_Guids(string input, string expected)
        {
            var method = typeof(LoggingHttpHandler).GetMethod("GetPathAndQuery", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);

            var result = (string)method!.Invoke(null, [new Uri(input)])!;

            Assert.Equal(expected, result);
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
