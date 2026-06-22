// Copyright (C) Microsoft Corporation. All rights reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class PowerAppsClientTests
{
    private static PowerAppsContext Context => new()
    {
        AccessToken = "test-token",
        EnvironmentId = "test-env",
    };

    [Fact]
    public async Task ListConnectionsAsync_ReturnsConnections_OnSuccess()
    {
        const string json = "{\"value\":[{\"name\":\"conn1\",\"properties\":{\"displayName\":\"Conn 1\",\"statuses\":[{\"status\":\"Connected\"}]}}]}";
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        }));
        var client = new PowerAppsClient(new HttpClient(handler));

        var result = await client.ListConnectionsAsync(Context, "shared_office365users", CancellationToken.None);

        var connection = Assert.Single(result);
        Assert.Equal("conn1", connection.Name);
        Assert.Equal("Conn 1", connection.DisplayName);
        Assert.Equal("Connected", connection.Status);
    }

    [Fact]
    public async Task ListConnectionsAsync_FollowsNextLinkAcrossPages()
    {
        const string page1 = "{\"value\":[{\"name\":\"conn1\",\"properties\":{\"displayName\":\"Conn 1\"}}],\"@odata.nextLink\":\"https://api.powerapps.com/page2\"}";
        const string page2 = "{\"value\":[{\"name\":\"conn2\",\"properties\":{\"displayName\":\"Conn 2\"}}]}";
        var requestUris = new List<string>();
        var handler = new StubHandler((request, _) =>
        {
            var uri = request.RequestUri!.ToString();
            requestUris.Add(uri);
            var body = uri.Contains("page2", StringComparison.OrdinalIgnoreCase) ? page2 : page1;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        });
        var client = new PowerAppsClient(new HttpClient(handler));

        var result = await client.ListConnectionsAsync(Context, "shared_office365users", CancellationToken.None);

        Assert.Equal(new[] { "conn1", "conn2" }, result.Select(c => c.Name).ToArray());
        Assert.Equal(2, requestUris.Count);
        Assert.Contains(requestUris, u => u.Contains("page2", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ListConnectionsAsync_RequestExceedsTimeout_ThrowsTimeoutException()
    {
        var handler = new StubHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var client = new PowerAppsClient(new HttpClient(handler), requestTimeout: TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAsync<TimeoutException>(
            () => client.ListConnectionsAsync(Context, "shared_office365users", CancellationToken.None));
    }

    [Fact]
    public async Task ListConnectionsAsync_CallerCancels_ThrowsOperationCanceledNotTimeout()
    {
        var handler = new StubHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var client = new PowerAppsClient(new HttpClient(handler), requestTimeout: TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.ListConnectionsAsync(Context, "shared_office365users", cts.Token));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return _handler(request, cancellationToken);
        }
    }
}
