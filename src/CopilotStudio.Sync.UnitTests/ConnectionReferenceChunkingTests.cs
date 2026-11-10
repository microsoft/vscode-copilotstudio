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
    private const string SecondPage = "https://test.crm.dynamics.com/api/data/v9.2/botcomponentcollections?page=2";

    private const string PageWithoutValueArray = "{\"unexpected\":true}";

    private const string PageLinkingToItself = "{\"value\":[],\"@odata.nextLink\":\"" + SecondPage + "\"}";

    private const string PageWithANullRow = "{\"value\":[null]}";

    [Fact]
    public async Task GetConnectionReferencesByLogicalNames_ChunksAtBatchSize_AndUnionsResults()
    {
        var names = Enumerable.Range(0, 120).Select(i => $"cr.shared_x.{i:D3}").ToList();
        var namesPerRequest = new List<int>();

        var handler = new StubHandler((request, _) =>
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

        var accessor = new Mock<IDataverseHttpClientAccessor>();
        accessor.Setup(a => a.CreateClient()).Returns(new HttpClient(handler));
        var client = new SyncDataverseClient(accessor.Object);
        client.SetDataverseUrl("https://test.crm.dynamics.com");

        var result = await client.GetConnectionReferencesByLogicalNamesAsync(new[] { "cr.a" }, CancellationToken.None);

        Assert.Equal(new[] { "cr.a", "cr.b" }, result.Select(r => r.ConnectionReferenceLogicalName).ToArray());
        Assert.Equal(2, requestUris.Count);
        Assert.Contains(requestUris, u => u.Contains("page2", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetLinkedAgentsByComponentCollectionSchemaNames_ChunksAtBatchSize_AndExpandsTheLinkedAgents()
    {
        var names = Enumerable.Range(0, 120).Select(index => $"cr_tools{index:D3}").Concat(new[] { "CR_TOOLS000", " cr_tools001 ", " ", string.Empty }).ToList();
        var namesPerRequest = new List<int>();
        var queries = new List<string>();

        var handler = new StubHandler((request, _) =>
        {
            var query = Uri.UnescapeDataString(request.RequestUri!.Query);
            queries.Add(query);
            var matched = Regex.Matches(query, @"schemaname eq '([^']*)'").Select(match => match.Groups[1].Value).ToList();
            namesPerRequest.Add(matched.Count);

            var entries = string.Join(",", matched.Select(name => $"{{\"schemaname\":\"{name}\",\"bot_botcomponentcollection\":[{{\"botid\":\"{Guid.NewGuid()}\",\"schemaname\":\"cr_agent_{name}\",\"name\":\"Agent {name}\"}},{{\"botid\":\"{Guid.Empty}\",\"schemaname\":\"cr_ghost\"}}]}}"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"{{\"value\":[{entries}]}}", Encoding.UTF8, "application/json"),
            });
        });

        var accessor = new Mock<IDataverseHttpClientAccessor>();
        accessor.Setup(clientAccessor => clientAccessor.CreateClient()).Returns(new HttpClient(handler));
        var client = new SyncDataverseClient(accessor.Object);
        client.SetDataverseUrl("https://test.crm.dynamics.com");

        var result = await client.GetLinkedAgentsByComponentCollectionSchemaNamesAsync(names, CancellationToken.None);

        Assert.Equal(new[] { 50, 50, 20 }, namesPerRequest);
        Assert.All(queries, query => Assert.Contains("$expand=bot_botcomponentcollection($select=botid,schemaname,name)", query, StringComparison.Ordinal));
        Assert.Equal(120, result.Count);
        var agent = Assert.Single(result["CR_TOOLS007"]);
        Assert.Equal(("cr_agent_cr_tools007", "Agent cr_tools007"), (agent.SchemaName, agent.DisplayName));
    }

    [Fact]
    public async Task GetLinkedAgentsByComponentCollectionSchemaNames_FollowsODataNextLink_AndSendsNothingForNoNames()
    {
        var requestUris = new List<string>();
        var handler = new StubHandler((request, _) =>
        {
            var uri = request.RequestUri!.ToString();
            requestUris.Add(uri);
            var body = uri.Contains("page2", StringComparison.OrdinalIgnoreCase)
                ? "{\"value\":[{\"schemaname\":\"cr_beta\",\"bot_botcomponentcollection\":[]}]}"
                : "{\"value\":[{\"schemaname\":\"cr_alpha\"}],\"@odata.nextLink\":\"https://test.crm.dynamics.com/api/data/v9.2/botcomponentcollections?page2=1\"}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        });

        var accessor = new Mock<IDataverseHttpClientAccessor>();
        accessor.Setup(clientAccessor => clientAccessor.CreateClient()).Returns(new HttpClient(handler));
        var client = new SyncDataverseClient(accessor.Object);
        client.SetDataverseUrl("https://test.crm.dynamics.com");

        Assert.Empty(await client.GetLinkedAgentsByComponentCollectionSchemaNamesAsync(Array.Empty<string>(), CancellationToken.None));
        Assert.Empty(await client.GetLinkedAgentsByComponentCollectionSchemaNamesAsync(null!, CancellationToken.None));
        Assert.Empty(requestUris);

        var result = await client.GetLinkedAgentsByComponentCollectionSchemaNamesAsync(new[] { "cr_alpha", "cr_beta" }, CancellationToken.None);

        Assert.Equal(new[] { "cr_alpha", "cr_beta" }, result.Keys.OrderBy(name => name, StringComparer.Ordinal));
        Assert.All(result.Values, Assert.Empty);
        Assert.Equal(2, requestUris.Count);
    }

    [Theory]
    [InlineData(nameof(SyncDataverseClient.GetComponentCollectionsBySchemaNamesAsync), PageWithoutValueArray, "The service did not return a collection page carrying a 'value' array, so the read failed rather than returning only the rows before it.")]
    [InlineData(nameof(SyncDataverseClient.GetLinkedAgentsByComponentCollectionSchemaNamesAsync), PageWithoutValueArray, "The service did not return a collection page carrying a 'value' array, so the read failed rather than returning only the rows before it.")]
    [InlineData(nameof(SyncDataverseClient.GetComponentCollectionsBySchemaNamesAsync), PageLinkingToItself, "The service returned a pagination link that had already been followed.")]
    [InlineData(nameof(SyncDataverseClient.GetLinkedAgentsByComponentCollectionSchemaNamesAsync), PageLinkingToItself, "The service returned a pagination link that had already been followed.")]
    [InlineData(nameof(SyncDataverseClient.GetComponentCollectionsBySchemaNamesAsync), PageWithANullRow, "The service returned a null component collection row, so the component collections could not be read.")]
    [InlineData(nameof(SyncDataverseClient.GetLinkedAgentsByComponentCollectionSchemaNamesAsync), PageWithANullRow, "The service returned a null component collection row, so the component collections could not be read.")]
    public async Task ReadsBySchemaName_WhenALaterPageLoopsCarriesNoValueArrayOrHoldsANullRow_FailInsteadOfReturningTheRowsBeforeIt(string read, string laterPage, string message)
    {
        var requests = 0;
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(++requests == 1 ? $"{{\"value\":[{{\"schemaname\":\"cr_alpha\",\"botcomponentcollectionid\":\"{Guid.NewGuid()}\",\"bot_botcomponentcollection\":[]}}],\"@odata.nextLink\":\"{SecondPage}\"}}" : requests == 2 ? laterPage : "{\"value\":[]}", Encoding.UTF8, "application/json"),
        }));

        var accessor = new Mock<IDataverseHttpClientAccessor>();
        accessor.Setup(clientAccessor => clientAccessor.CreateClient()).Returns(new HttpClient(handler));
        var client = new SyncDataverseClient(accessor.Object);
        client.SetDataverseUrl("https://test.crm.dynamics.com");

        var failure = await Assert.ThrowsAsync<DataverseRequestException>(() => read == nameof(SyncDataverseClient.GetComponentCollectionsBySchemaNamesAsync) ? client.GetComponentCollectionsBySchemaNamesAsync(new[] { "cr_alpha" }, CancellationToken.None) : (Task)client.GetLinkedAgentsByComponentCollectionSchemaNamesAsync(new[] { "cr_alpha" }, CancellationToken.None));

        Assert.Equal((HttpStatusCode.BadGateway, message), (failure.StatusCode, failure.ResponseBody));
        Assert.Equal(2, requests);
    }

    internal sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) => _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _handler(request, cancellationToken);
    }
}
