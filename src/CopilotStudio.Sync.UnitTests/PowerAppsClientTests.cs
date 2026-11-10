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
    private const string PowerPlatformApiEndpoint = "https://testenv.environment.api.test.powerplatform.com/";

    private const string PowerAppsApiRoot = "https://api.powerapps.com/providers/Microsoft.PowerApps/apis";

    private const string PowerPlatformApiRoot = "https://testenv.environment.api.test.powerplatform.com/connectivity/connectors";

    private const string EnvironmentFilter = "&$filter=environment%20eq%20%27test-env%27";

    private const string EmptyList = "{\"value\":[]}";

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
            var uri = (request.RequestUri ?? throw new Xunit.Sdk.XunitException("Expected a non-null test value.")).ToString();
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

    [Fact]
    public async Task ListConnectorsAsync_FollowsEveryPageAndPreservesCatalogIdsAndIcons()
    {
        var pages = new Queue<string>(new[]
        {
            "{\"value\":[{\"name\":\"shared_teams\",\"properties\":{\"iconUri\":\"https://catalog.example/teams.png\"}}],\"@odata.nextLink\":\"https://api.powerapps.com/page2\"}",
            "{\"value\":[{\"name\":\"custom-connector\",\"properties\":{\"iconUri\":\"https://catalog.example/custom.png\"}}]}",
        });
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(pages.Dequeue(), Encoding.UTF8, "application/json"),
        }));
        var client = new PowerAppsClient(new HttpClient(handler));

        var result = await client.ListConnectorsAsync(Context, CancellationToken.None);

        Assert.Empty(pages);
        Assert.Equal(new[] { "shared_teams", "custom-connector" }, result.Select(connector => connector.InternalId));
        Assert.Equal(new[] { "https://catalog.example/teams.png", "https://catalog.example/custom.png" }, result.Select(connector => connector.IconUri));
    }

    [Fact]
    public async Task ListConnectorsAsync_WhenPageLimitIsReached_ReturnsCollectedConnectors()
    {
        var requests = 0;
        var handler = new StubHandler((_, _) =>
        {
            requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""{"value":[{"name":"shared_test_{{requests}}"}],"@odata.nextLink":"https://api.powerapps.com/more"}""",
                    Encoding.UTF8,
                    "application/json"),
            });
        });
        var client = new PowerAppsClient(new HttpClient(handler));

        var result = await client.ListConnectorsAsync(Context, CancellationToken.None);

        Assert.Equal(50, requests);
        Assert.Equal(Enumerable.Range(1, 50).Select(page => $"shared_test_{page}"), result.Select(connector => connector.InternalId));
    }

    public static TheoryData<string, bool, string> EveryConnectionCall()
    {
        var data = new TheoryData<string, bool, string>();

        foreach (var throughPowerPlatformApi in new[] { false, true })
        {
            foreach (var (call, pathSuffix) in new[] { ("ListConnectors", string.Empty), ("ListConnections", "/shared_teams/connections") })
            {
                data.Add(call, throughPowerPlatformApi, throughPowerPlatformApi ? $"{PowerPlatformApiRoot}{pathSuffix}?api-version=1{EnvironmentFilter}" : $"{PowerAppsApiRoot}{pathSuffix}?api-version=2016-11-01{EnvironmentFilter}");
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryConnectionCall))]
    public async Task EveryConnectionCall_SendsTheRequestOfTheContextsEndpointFamily(string call, bool throughPowerPlatformApi, string uri)
    {
        var requests = new List<RecordedRequest>();
        var client = new PowerAppsClient(new HttpClient(Recording(requests)));

        await (call == "ListConnectors" ? (Task)client.ListConnectorsAsync(ContextFor(throughPowerPlatformApi), CancellationToken.None) : client.ListConnectionsAsync(ContextFor(throughPowerPlatformApi), "shared_teams", CancellationToken.None));

        Assert.Equal(new RecordedRequest("GET", uri, "Bearer test-token", "CopilotStudio.Sync"), Assert.Single(requests));
    }

    [Fact]
    public async Task ConnectionCalls_WithAnEndpoint_IgnoreTheClusterCategory()
    {
        var requests = new List<RecordedRequest>();
        var client = new PowerAppsClient(new HttpClient(Recording(requests)));

        await client.ListConnectionsAsync(new PowerAppsContext { AccessToken = "test-token", EnvironmentId = "test-env", ClusterCategory = CoreServicesClusterCategory.Mooncake }, "shared_teams", CancellationToken.None);
        await client.ListConnectionsAsync(new PowerAppsContext { AccessToken = "test-token", EnvironmentId = "test-env", ClusterCategory = CoreServicesClusterCategory.Mooncake, PowerPlatformApiEndpoint = new Uri(PowerPlatformApiEndpoint) }, "shared_teams", CancellationToken.None);

        Assert.Equal(new[] { "https://api.powerapps.cn/providers/Microsoft.PowerApps/apis/shared_teams/connections", $"{PowerPlatformApiRoot}/shared_teams/connections" }, requests.Select(request => request.Uri.Split('?')[0]));
    }

    [Theory]
    [InlineData("https://testenv.environment.api.test.powerplatform.com", "https://testenv.environment.api.test.powerplatform.com/connectivity/connectors")]
    [InlineData("HTTPS://TestEnv.Environment.Api.Test.PowerPlatform.com:443/", "https://testenv.environment.api.test.powerplatform.com/connectivity/connectors")]
    [InlineData("https://gateway.example/environments/test-env/", "https://gateway.example/environments/test-env/connectivity/connectors")]
    public async Task ConnectionCalls_AppendTheConnectorsPathToTheEndpoint(string endpoint, string root)
    {
        var requests = new List<RecordedRequest>();

        await new PowerAppsClient(new HttpClient(Recording(requests))).ListConnectorsAsync(new PowerAppsContext { AccessToken = "test-token", EnvironmentId = "test-env", PowerPlatformApiEndpoint = new Uri(endpoint) }, CancellationToken.None);

        Assert.Equal($"{root}?api-version=1{EnvironmentFilter}", Assert.Single(requests).Uri);
    }

    [Theory]
    [InlineData("http://testenv.environment.api.test.powerplatform.com/")]
    [InlineData("https://testenv.environment.api.test.powerplatform.com/?api-version=1")]
    [InlineData("https://testenv.environment.api.test.powerplatform.com/#connectors")]
    [InlineData("connectivity/connectors")]
    public void PowerPlatformApiEndpoint_ThatIsNotAnAbsoluteHttpsBase_IsRejected(string endpoint)
    {
        Assert.Equal(nameof(PowerAppsContext.PowerPlatformApiEndpoint), Assert.Throws<ArgumentException>(() => new PowerAppsContext { PowerPlatformApiEndpoint = new Uri(endpoint, UriKind.RelativeOrAbsolute) }).ParamName);
    }

    private static PowerAppsContext ContextFor(bool throughPowerPlatformApi) => new()
    {
        AccessToken = "test-token",
        EnvironmentId = "test-env",
        PowerPlatformApiEndpoint = throughPowerPlatformApi ? new Uri(PowerPlatformApiEndpoint) : null,
    };

    private static StubHandler Recording(List<RecordedRequest> requests) => new((request, _) =>
    {
        requests.Add(new RecordedRequest(request.Method.Method, request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString(), request.Headers.UserAgent.ToString()));

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(EmptyList, Encoding.UTF8, "application/json") });
    });

    private sealed record RecordedRequest(string Method, string Uri, string? Authorization, string UserAgent);

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
