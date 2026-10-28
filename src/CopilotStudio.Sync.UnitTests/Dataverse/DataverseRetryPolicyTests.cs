// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.Platform.Content.Abstractions;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class DataverseRetryPolicyTests
{
    private static readonly Guid ComponentId = Guid.Parse("6f1c2a4e-0b7d-4c55-9a43-2d8e5f7b9c10");

    private static readonly HttpRequestOptionsKey<string> TraceOption = new("trace");

    private sealed record SentRequest(HttpMethod Method, string Url, string? ClientRequestId, string? Authorization, string? Trace, string? ContentType, byte[] Body);

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;

        public ScriptedHandler(params HttpResponseMessage[] responses) => _responses = new Queue<HttpResponseMessage>(responses);

        public int Attempts => Requests.Count;

        public List<SentRequest> Requests { get; } = new();

        public Action? OnSend { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new SentRequest(
                request.Method,
                (request.RequestUri ?? throw new Xunit.Sdk.XunitException("Expected a non-null test value.")).ToString(),
                request.Headers.TryGetValues("x-ms-client-request-id", out var clientRequestIds) ? string.Join(",", clientRequestIds) : null,
                request.Headers.Authorization?.ToString(),
                request.Options.TryGetValue(TraceOption, out var trace) ? trace : null,
                request.Content?.Headers.ContentType?.ToString(),
                request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken)));
            OnSend?.Invoke();

            return _responses.Dequeue();
        }
    }

    private sealed class ForwardOnlyStream(byte[] content) : MemoryStream(content)
    {
        public override bool CanSeek => false;

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    }

    private static HttpResponseMessage Status(HttpStatusCode statusCode, string body = "") =>
        new(statusCode) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Throttled(TimeSpan retryAfter)
    {
        var response = Status((HttpStatusCode)429);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter);

        return response;
    }

    private static DataverseRetryPolicy RecordingPolicy(List<TimeSpan> waits) =>
        new() { Delay = (delay, _) => { waits.Add(delay); return Task.CompletedTask; } };

    private static Task<HttpResponseMessage> SendAsync(DataverseRetryPolicy policy, ScriptedHandler handler, HttpMethod method, CancellationToken cancellationToken = default) =>
        policy.SendAsync(new HttpClient(handler), () => new HttpRequestMessage(method, "https://test.crm.dynamics.com/api/data/v9.2/workflows"), HttpCompletionOption.ResponseHeadersRead, cancellationToken);

    private static HttpClient ThroughHandler(DataverseRetryPolicy policy, ScriptedHandler handler) => new(new DataverseRetryHandler(policy) { InnerHandler = handler });

    private static SyncDataverseClient KnowledgeFileClient(ScriptedHandler handler)
    {
        var accessor = new Mock<IDataverseHttpClientAccessor>();
        accessor.Setup(a => a.CreateClient()).Returns(new HttpClient(handler));

        var client = new SyncDataverseClient(accessor.Object, "CopilotStudio.Sync", RecordingPolicy(new List<TimeSpan>()));
        client.SetDataverseUrl("https://test.crm.dynamics.com");

        return client;
    }

    [Fact]
    public async Task SendAsync_WhenTheServiceIsBrieflyUnavailable_SucceedsOnTheSecondAttempt()
    {
        var waits = new List<TimeSpan>();
        var handler = new ScriptedHandler(Status(HttpStatusCode.ServiceUnavailable), Status(HttpStatusCode.OK));

        using var response = await SendAsync(RecordingPolicy(waits), handler, HttpMethod.Get);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.Attempts);
        Assert.Single(waits);
    }

    [Fact]
    public async Task SendAsync_WhenThrottled_WaitsAsLongAsTheServiceAsks()
    {
        var waits = new List<TimeSpan>();

        using var response = await SendAsync(RecordingPolicy(waits), new ScriptedHandler(Throttled(TimeSpan.FromSeconds(2)), Status(HttpStatusCode.OK)), HttpMethod.Get);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([TimeSpan.FromSeconds(2)], waits);
    }

    [Fact]
    public async Task SendAsync_WhenThrottledUntilATimeAlreadyPast_RetriesWithoutWaiting()
    {
        var waits = new List<TimeSpan>();
        var throttled = Status((HttpStatusCode)429);
        throttled.Headers.RetryAfter = new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddMinutes(-1));

        using var response = await SendAsync(RecordingPolicy(waits), new ScriptedHandler(throttled, Status(HttpStatusCode.OK)), HttpMethod.Get);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([TimeSpan.Zero], waits);
    }

    [Fact]
    public async Task SendAsync_NeverWaitsLongerThanTheMaximumWhateverTheServiceAsks()
    {
        var waits = new List<TimeSpan>();

        using var response = await SendAsync(RecordingPolicy(waits), new ScriptedHandler(Throttled(TimeSpan.FromMinutes(10)), Status(HttpStatusCode.OK)), HttpMethod.Patch);

        Assert.Equal([TimeSpan.FromSeconds(30)], waits);
    }

    [Fact]
    public async Task SendAsync_WithoutAnAskedForWait_BacksOffExponentiallyWithJitter()
    {
        var waits = new List<TimeSpan>();
        var handler = new ScriptedHandler(Status(HttpStatusCode.BadGateway), Status(HttpStatusCode.GatewayTimeout), Status(HttpStatusCode.OK));

        using var response = await SendAsync(RecordingPolicy(waits), handler, HttpMethod.Get);

        Assert.Equal(2, waits.Count);
        Assert.InRange(waits[0], TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1));
        Assert.InRange(waits[1], TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task SendAsync_NeverBacksOffLongerThanTheMaximum()
    {
        var waits = new List<TimeSpan>();
        var policy = new DataverseRetryPolicy { BaseDelay = TimeSpan.FromHours(1), MaximumDelay = TimeSpan.FromSeconds(2), Delay = (delay, _) => { waits.Add(delay); return Task.CompletedTask; } };

        using var response = await SendAsync(policy, new ScriptedHandler(Status(HttpStatusCode.ServiceUnavailable), Status(HttpStatusCode.OK)), HttpMethod.Get);

        Assert.InRange(Assert.Single(waits), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task SendAsync_WhenTheServiceStaysUnavailable_ReturnsTheLastFailureAfterThreeAttempts()
    {
        var waits = new List<TimeSpan>();
        var handler = new ScriptedHandler(Status(HttpStatusCode.ServiceUnavailable), Status(HttpStatusCode.ServiceUnavailable), Status(HttpStatusCode.GatewayTimeout, "last"), Status(HttpStatusCode.OK));

        using var response = await SendAsync(RecordingPolicy(waits), handler, HttpMethod.Get);

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Equal("last", await response.Content.ReadAsStringAsync());
        Assert.Equal(3, handler.Attempts);
        Assert.Equal(2, waits.Count);
    }

    [Theory]
    [InlineData("0x80040251")]
    [InlineData("0x80044151")]
    [InlineData("0x8004026C")]
    [InlineData("0x8004026c")]
    [InlineData("0x8004ED47")]
    [InlineData("0x80048D3D")]
    [InlineData("0x80098019")]
    public async Task SendAsync_RetriesAnInternalErrorWhoseCodeIsTransient(string code)
    {
        var handler = new ScriptedHandler(Status(HttpStatusCode.InternalServerError, $"{{\"error\":{{\"code\":\"{code}\",\"message\":\"The service failed.\"}}}}"), Status(HttpStatusCode.OK));

        using var response = await SendAsync(RecordingPolicy(new List<TimeSpan>()), handler, HttpMethod.Get);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.Attempts);
    }

    [Theory]
    [InlineData("{\"error\":{\"code\":\"0x80040216\",\"message\":\"An unexpected error occurred.\"}}")]
    [InlineData("{\"error\":{\"message\":\"An unexpected error occurred.\"}}")]
    [InlineData("{\"error\":{\"code\":80040251}}")]
    [InlineData("{\"error\":\"0x80040251\"}")]
    [InlineData("[\"0x80040251\"]")]
    [InlineData("An unexpected error occurred.")]
    [InlineData("")]
    public async Task SendAsync_ReportsAnInternalErrorWithoutATransientCodeAtOnce(string body)
    {
        var waits = new List<TimeSpan>();
        var handler = new ScriptedHandler(Status(HttpStatusCode.InternalServerError, body), Status(HttpStatusCode.OK));

        using var response = await SendAsync(RecordingPolicy(waits), handler, HttpMethod.Get);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(body, await response.Content.ReadAsStringAsync());
        Assert.Equal(1, handler.Attempts);
        Assert.Empty(waits);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.PreconditionFailed)]
    [InlineData(HttpStatusCode.NotImplemented)]
    public async Task SendAsync_ReportsAFailureThatIsNotTransientAtOnce(HttpStatusCode statusCode)
    {
        var handler = new ScriptedHandler(Status(statusCode), Status(HttpStatusCode.OK));

        using var response = await SendAsync(RecordingPolicy(new List<TimeSpan>()), handler, HttpMethod.Delete);

        Assert.Equal(statusCode, response.StatusCode);
        Assert.Equal(1, handler.Attempts);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway, "")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "")]
    [InlineData(HttpStatusCode.GatewayTimeout, "")]
    [InlineData(HttpStatusCode.InternalServerError, "{\"error\":{\"code\":\"0x80040251\",\"message\":\"The service failed.\"}}")]
    public async Task SendAsync_NeverRepeatsACreateAfterAServerFailure(HttpStatusCode statusCode, string body)
    {
        var waits = new List<TimeSpan>();
        var handler = new ScriptedHandler(Status(statusCode, body), Status(HttpStatusCode.OK));

        using var response = await SendAsync(RecordingPolicy(waits), handler, HttpMethod.Post);

        Assert.Equal(statusCode, response.StatusCode);
        Assert.Equal(1, handler.Attempts);
        Assert.Empty(waits);
    }

    [Fact]
    public async Task SendAsync_RepeatsAThrottledCreateOnceTheServiceAllows()
    {
        var waits = new List<TimeSpan>();
        var handler = new ScriptedHandler(Throttled(TimeSpan.FromSeconds(2)), Status(HttpStatusCode.Created));

        using var response = await SendAsync(RecordingPolicy(waits), handler, HttpMethod.Post);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(2, handler.Attempts);
        Assert.Equal([TimeSpan.FromSeconds(2)], waits);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task SendAsync_RepeatsAServerFailureForAMethodSafeToRepeat(string method)
    {
        var handler = new ScriptedHandler(Status(HttpStatusCode.BadGateway), Status(HttpStatusCode.OK));

        using var response = await SendAsync(RecordingPolicy(new List<TimeSpan>()), handler, new HttpMethod(method));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.Attempts);
    }

    [Fact]
    public async Task SendAsync_WhenTheConnectionFails_DoesNotSendAgain()
    {
        var handler = new ScriptedHandler(Status(HttpStatusCode.OK)) { OnSend = () => throw new HttpRequestException("The connection was reset.") };

        await Assert.ThrowsAsync<HttpRequestException>(() => SendAsync(RecordingPolicy(new List<TimeSpan>()), handler, HttpMethod.Get));

        Assert.Equal(1, handler.Attempts);
    }

    [Fact]
    public async Task SendAsync_WhenCancelledWhileWaiting_StopsWithoutAnotherAttempt()
    {
        using var cancellation = new CancellationTokenSource();
        var policy = new DataverseRetryPolicy { BaseDelay = TimeSpan.FromHours(1), MaximumDelay = TimeSpan.FromHours(1) };
        var handler = new ScriptedHandler(Status(HttpStatusCode.ServiceUnavailable), Status(HttpStatusCode.OK)) { OnSend = () => cancellation.CancelAfter(TimeSpan.FromMilliseconds(50)) };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SendAsync(policy, handler, HttpMethod.Get, cancellation.Token));

        Assert.Equal(1, handler.Attempts);
    }

    [Fact]
    public async Task SendAsync_StampsOneClientRequestIdPerLogicalRequestOnEveryAttempt()
    {
        var policy = RecordingPolicy(new List<TimeSpan>());
        var handler = new ScriptedHandler(Status(HttpStatusCode.ServiceUnavailable), Status(HttpStatusCode.OK), Status(HttpStatusCode.ServiceUnavailable), Status(HttpStatusCode.OK));

        (await SendAsync(policy, handler, HttpMethod.Get)).Dispose();
        (await SendAsync(policy, handler, HttpMethod.Delete)).Dispose();

        var clientRequestIds = handler.Requests.Select(request => request.ClientRequestId).ToArray();
        Assert.True(Guid.TryParse(clientRequestIds[0], out _));
        Assert.Equal(clientRequestIds[0], clientRequestIds[1]);
        Assert.Equal(clientRequestIds[2], clientRequestIds[3]);
        Assert.NotEqual(clientRequestIds[0], clientRequestIds[2]);
    }

    [Fact]
    public async Task SendAsync_KeepsTheClientRequestIdTheCallerSet()
    {
        var handler = new ScriptedHandler(Status(HttpStatusCode.ServiceUnavailable), Status(HttpStatusCode.OK));

        using var response = await RecordingPolicy(new List<TimeSpan>()).SendAsync(new HttpClient(handler), () => new HttpRequestMessage(HttpMethod.Get, "https://test.crm.dynamics.com/api/data/v9.2/workflows") { Headers = { { "x-ms-client-request-id", "caller-request" } } }, HttpCompletionOption.ResponseHeadersRead, CancellationToken.None);

        Assert.Equal(["caller-request", "caller-request"], handler.Requests.Select(request => request.ClientRequestId));
    }

    [Fact]
    public async Task DownloadKnowledgeFileAsync_ReadsTheFileAgainUnderOneRequestIdWhenTheServiceIsBrieflyUnavailable()
    {
        var handler = new ScriptedHandler(Status(HttpStatusCode.ServiceUnavailable), new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
        using var destination = new MemoryStream();

        await KnowledgeFileClient(handler).DownloadKnowledgeFileAsync(destination, new BotComponentId(ComponentId), CancellationToken.None);

        Assert.Equal([1, 2, 3], destination.ToArray());
        Assert.Equal(2, handler.Attempts);
        Assert.All(handler.Requests, request => Assert.Equal($"https://test.crm.dynamics.com/api/data/v9.2/botcomponents({ComponentId})/filedata/$value", request.Url));
        Assert.NotNull(Assert.Single(handler.Requests.Select(request => request.ClientRequestId).Distinct()));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UploadKnowledgeFileAsync_SendsTheSameBytesAgainUnderOneRequestIdWhenTheServiceIsBrieflyUnavailable(bool seekable)
    {
        var content = Encoding.UTF8.GetBytes("knowledge the agent answers from");
        var handler = new ScriptedHandler(Status(HttpStatusCode.ServiceUnavailable), Status(HttpStatusCode.NoContent));
        using var source = seekable ? new MemoryStream(content) : new ForwardOnlyStream(content);

        await KnowledgeFileClient(handler).UploadKnowledgeFileAsync(source, ComponentId, "Rates.pdf", CancellationToken.None);

        Assert.Equal(2, handler.Attempts);
        Assert.All(handler.Requests, request => Assert.Equal((HttpMethod.Patch, $"https://test.crm.dynamics.com/api/data/v9.2/botcomponents({ComponentId})/filedata/", Encoding.UTF8.GetString(content)), (request.Method, request.Url, Encoding.UTF8.GetString(request.Body))));
        Assert.NotNull(Assert.Single(handler.Requests.Select(request => request.ClientRequestId).Distinct()));
    }

    [Fact]
    public async Task UploadKnowledgeFileAsync_SendsAgainFromWhereTheCallerLeftTheStream()
    {
        var handler = new ScriptedHandler(Status(HttpStatusCode.ServiceUnavailable), Status(HttpStatusCode.NoContent));
        using var source = new MemoryStream(Encoding.UTF8.GetBytes("header|knowledge"));
        source.Position = "header|".Length;

        await KnowledgeFileClient(handler).UploadKnowledgeFileAsync(source, ComponentId, "Rates.pdf", CancellationToken.None);

        Assert.Equal(["knowledge", "knowledge"], handler.Requests.Select(request => Encoding.UTF8.GetString(request.Body)));
    }

    [Fact]
    public async Task UploadKnowledgeFileAsync_ReportsTheFailureOnceTheServiceStaysUnavailable()
    {
        var handler = new ScriptedHandler(Status(HttpStatusCode.ServiceUnavailable), Status(HttpStatusCode.ServiceUnavailable), Status(HttpStatusCode.ServiceUnavailable, "{\"error\":{\"message\":\"Try again later.\"}}"));
        using var source = new MemoryStream(Encoding.UTF8.GetBytes("knowledge"));

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => KnowledgeFileClient(handler).UploadKnowledgeFileAsync(source, ComponentId, "Rates.pdf", CancellationToken.None));

        Assert.Equal("Dataverse upload failed (503): {\"error\":{\"message\":\"Try again later.\"}}", failure.Message);
        Assert.Equal(3, handler.Attempts);
    }

    [Fact]
    public async Task Handler_RepeatsATransientReadUnderTheRequestIdTheCallerSet()
    {
        var handler = new ScriptedHandler(Status(HttpStatusCode.ServiceUnavailable), Status(HttpStatusCode.OK));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://test.crm.dynamics.com/api/data/v9.2/bots") { Headers = { { "x-ms-client-request-id", "caller-request" } } };

        using var response = await ThroughHandler(RecordingPolicy(new List<TimeSpan>()), handler).SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["caller-request", "caller-request"], handler.Requests.Select(sent => sent.ClientRequestId));
        Assert.Same(request, response.RequestMessage);
    }

    [Fact]
    public async Task Handler_CarriesTheCallersHeadersAndOptionsToEveryAttempt()
    {
        var handler = new ScriptedHandler(Status(HttpStatusCode.BadGateway), Status(HttpStatusCode.OK));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://test.crm.dynamics.com/api/data/v9.2/bots") { Headers = { Authorization = new AuthenticationHeaderValue("Bearer", "token") } };
        request.Options.Set(TraceOption, "on");

        using var response = await ThroughHandler(RecordingPolicy(new List<TimeSpan>()), handler).SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.Attempts);
        Assert.All(handler.Requests, sent => Assert.Equal(("Bearer token", "on"), (sent.Authorization, sent.Trace)));
    }

    [Fact]
    public async Task Handler_RepeatsAThrottledCreateWithTheSameBodyUnderOneRequestId()
    {
        var waits = new List<TimeSpan>();
        var handler = new ScriptedHandler(Throttled(TimeSpan.FromSeconds(2)), Status(HttpStatusCode.Created));
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://test.crm.dynamics.com/api/data/v9.2/bots") { Content = new StringContent("{\"name\":\"Agent\"}", Encoding.UTF8, "application/json") };

        using var response = await ThroughHandler(RecordingPolicy(waits), handler).SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal([TimeSpan.FromSeconds(2)], waits);
        Assert.Equal(2, handler.Attempts);
        Assert.All(handler.Requests, sent => Assert.Equal(("application/json; charset=utf-8", "{\"name\":\"Agent\"}"), (sent.ContentType, Encoding.UTF8.GetString(sent.Body))));
        Assert.True(Guid.TryParse(Assert.Single(handler.Requests.Select(sent => sent.ClientRequestId).Distinct()), out _));
        Assert.Equal("{\"name\":\"Agent\"}", await request.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Handler_NeverRepeatsACreateAfterAServerFailure()
    {
        var handler = new ScriptedHandler(Status(HttpStatusCode.ServiceUnavailable), Status(HttpStatusCode.Created));

        using var response = await ThroughHandler(RecordingPolicy(new List<TimeSpan>()), handler).PostAsync("https://test.crm.dynamics.com/api/data/v9.2/bots", new StringContent("{}"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, handler.Attempts);
    }

    [Fact]
    public async Task Handler_PassesEachAttemptOfARequestThePolicySendsThroughOnce()
    {
        var waits = new List<TimeSpan>();
        var policy = RecordingPolicy(waits);
        var handler = new ScriptedHandler(Status(HttpStatusCode.ServiceUnavailable), Status(HttpStatusCode.ServiceUnavailable), Status(HttpStatusCode.ServiceUnavailable));

        using var response = await policy.SendAsync(ThroughHandler(policy, handler), () => new HttpRequestMessage(HttpMethod.Get, "https://test.crm.dynamics.com/api/data/v9.2/workflows"), HttpCompletionOption.ResponseHeadersRead, CancellationToken.None);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(3, handler.Attempts);
        Assert.Equal(2, waits.Count);
        Assert.Single(handler.Requests.Select(sent => sent.ClientRequestId).Distinct());
    }
}
