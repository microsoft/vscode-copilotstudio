// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.Platform.Content.Abstractions;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class ConnectionReferenceChunkingTests
{
    [Fact]
    public async Task GetConnectionReferencesByLogicalNames_ChunksAtBatchSize_AndUnionsResults()
    {
        var names = Enumerable.Range(0, 120).Select(i => $"cr.shared_x.{i:D3}").ToList();
        var namesPerRequest = new List<int>();

        var handler = new StubHandler((request, _) =>
        {
            var query = Uri.UnescapeDataString(request.RequestUri!.Query);
            var matched = Regex.Matches(query, @"connectionreferencelogicalname eq '([^']*)'")
                .Select(m => m.Groups[1].Value)
                .ToList();
            namesPerRequest.Add(matched.Count);

            var entries = string.Join(",", matched.Select(n =>
                $"{{\"connectionreferenceid\":\"{Guid.NewGuid()}\",\"connectionreferencelogicalname\":\"{n}\",\"connectorid\":\"/providers/x/{n}\",\"connectionid\":\"\"}}"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"{{\"value\":[{entries}]}}", Encoding.UTF8, "application/json"),
            });
        });

        var httpClient = new HttpClient(handler);
        var accessor = new Mock<IDataverseHttpClientAccessor>();
        accessor.Setup(a => a.CreateClient()).Returns(httpClient);
        var client = new SyncDataverseClient(accessor.Object);
        client.SetDataverseUrl("https://test.crm.dynamics.com");

        var result = await client.GetConnectionReferencesByLogicalNamesAsync(names, CancellationToken.None);

        Assert.Equal(3, namesPerRequest.Count);
        Assert.All(namesPerRequest, count => Assert.True(count <= 50, $"a single request carried {count} names, exceeding the 50 batch size"));
        Assert.Equal(120, namesPerRequest.Sum());

        Assert.Equal(
            names.OrderBy(n => n, StringComparer.Ordinal),
            result.Select(r => r.ConnectionReferenceLogicalName).OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public async Task GetConnectionReferencesByLogicalNames_FollowsODataNextLink()
    {
        var requestUris = new List<string>();
        var handler = new StubHandler((request, _) =>
        {
            var uri = request.RequestUri!.ToString();
            requestUris.Add(uri);
            var body = uri.Contains("page2", StringComparison.OrdinalIgnoreCase)
                ? "{\"value\":[{\"connectionreferencelogicalname\":\"cr.b\",\"connectorid\":\"/providers/x/b\",\"connectionid\":\"\"}]}"
                : "{\"value\":[{\"connectionreferencelogicalname\":\"cr.a\",\"connectorid\":\"/providers/x/a\",\"connectionid\":\"\"}],\"@odata.nextLink\":\"https://test.crm.dynamics.com/api/data/v9.2/connectionreferences?page2=1\"}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        });

        var accessor = new Mock<IDataverseHttpClientAccessor>();
        accessor.Setup(a => a.CreateClient()).Returns(new HttpClient(handler));
        var client = new SyncDataverseClient(accessor.Object);
        client.SetDataverseUrl("https://test.crm.dynamics.com");

        var result = await client.GetConnectionReferencesByLogicalNamesAsync(new[] { "cr.a" }, CancellationToken.None);

        Assert.Equal(new[] { "cr.a", "cr.b" }, result.Select(r => r.ConnectionReferenceLogicalName).ToArray());
        Assert.Equal(2, requestUris.Count);
        Assert.Contains(requestUris, u => u.Contains("page2", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) => _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _handler(request, cancellationToken);
    }
}
