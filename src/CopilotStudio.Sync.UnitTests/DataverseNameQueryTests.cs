// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.Platform.Content.Abstractions;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class DataverseNameQueryTests
{
    [Fact]
    public async Task GetConnectionReferencesByLogicalNames_ChunksAtBatchSize_AndUnionsResults()
    {
        var names = Enumerable.Range(0, 120).Select(i => $"cr.shared_x.{i:D3}").ToList();
        var namesPerRequest = new List<int>();

        var client = CreateClient((request, _) =>
        {
            var query = Uri.UnescapeDataString((request.RequestUri ?? throw new Xunit.Sdk.XunitException("Expected a non-null test value.")).Query);
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
        var client = CreateClient((request, _) =>
        {
            var uri = (request.RequestUri ?? throw new Xunit.Sdk.XunitException("Expected a non-null test value.")).ToString();
            requestUris.Add(uri);
            var body = uri.Contains("page2", StringComparison.OrdinalIgnoreCase)
                ? "{\"value\":[{\"connectionreferencelogicalname\":\"cr.b\",\"connectorid\":\"/providers/x/b\",\"connectionid\":\"\"}]}"
                : "{\"value\":[{\"connectionreferencelogicalname\":\"cr.a\",\"connectorid\":\"/providers/x/a\",\"connectionid\":\"\"}],\"@odata.nextLink\":\"https://test.crm.dynamics.com/api/data/v9.2/connectionreferences?page2=1\"}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        });

        var result = await client.GetConnectionReferencesByLogicalNamesAsync(new[] { "cr.a" }, CancellationToken.None);

        Assert.Equal(new[] { "cr.a", "cr.b" }, result.Select(r => r.ConnectionReferenceLogicalName).ToArray());
        Assert.Equal(2, requestUris.Count);
        Assert.Contains(requestUris, u => u.Contains("page2", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetEnvironmentVariableDefinitionsBySchemaNames_QueriesDistinctNamesInBatches_AndReadsTargetMetadata()
    {
        var names = Enumerable.Range(0, 60).Select(i => $"cr9f2_variable{i:D2}").ToList();
        var definitionId = Guid.NewGuid();
        var valueId = Guid.NewGuid();
        var requests = new List<string>();
        var client = CreateClient((request, _) =>
        {
            var requestUri = request.RequestUri ?? throw new Xunit.Sdk.XunitException("Expected a non-null test value.");
            var query = Uri.UnescapeDataString(requestUri.Query);
            requests.Add(requestUri.AbsolutePath + query);
            var entries = string.Join(",", Regex.Matches(query, @"schemaname eq '([^']*)'")
                .Select(m => m.Groups[1].Value)
                .Select(n => $"{{\"environmentvariabledefinitionid\":\"{definitionId}\",\"schemaname\":\"{n}\",\"ismanaged\":{(n.EndsWith('7') ? "true" : "false")},\"versionnumber\":200,\"environmentvariabledefinition_environmentvariablevalue\":[{{\"environmentvariablevalueid\":\"{valueId}\",\"versionnumber\":300}}]}}"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"{{\"value\":[{entries}]}}", Encoding.UTF8, "application/json"),
            });
        });

        var result = await client.GetEnvironmentVariableDefinitionsBySchemaNamesAsync(names.Concat(new[] { " ", "CR9F2_VARIABLE00" }), CancellationToken.None);

        Assert.Equal(2, requests.Count);
        Assert.All(requests, request =>
        {
            Assert.StartsWith("/api/data/v9.2/environmentvariabledefinitions?$select=environmentvariabledefinitionid,schemaname,ismanaged,versionnumber&$filter=", request, StringComparison.Ordinal);
            Assert.Contains("&$expand=environmentvariabledefinition_environmentvariablevalue($select=environmentvariablevalueid,versionnumber;$orderby=createdon desc;$top=1)", request, StringComparison.Ordinal);
        });
        Assert.Equal(names, result.Select(r => r.SchemaName).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(names.Where(n => n.EndsWith('7')), result.Where(r => r.IsManaged).Select(r => r.SchemaName).OrderBy(n => n, StringComparer.Ordinal));
        Assert.All(result, definition =>
        {
            Assert.Equal(definitionId, definition.DefinitionId);
            Assert.Equal(200, definition.Version);
            var value = Assert.Single(definition.Values);
            Assert.Equal(valueId, value.ValueId);
            Assert.Equal(300, value.Version);
        });
    }

    [Fact]
    public async Task GetEnvironmentVariableDefinitionsBySchemaNames_WithoutNames_SendsNoRequest()
    {
        var requests = 0;
        var client = CreateClient((_, _) =>
        {
            requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        var result = await client.GetEnvironmentVariableDefinitionsBySchemaNamesAsync(new[] { string.Empty, " " }, CancellationToken.None);

        Assert.Empty(result);
        Assert.Equal(0, requests);
    }

    private static SyncDataverseClient CreateClient(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
    {
        var accessor = new Mock<IDataverseHttpClientAccessor>();
        accessor.Setup(a => a.CreateClient()).Returns(new HttpClient(new StubHandler(respond)));
        var client = new SyncDataverseClient(accessor.Object);
        client.SetDataverseUrl("https://test.crm.dynamics.com");
        return client;
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) => _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _handler(request, cancellationToken);
    }
}
