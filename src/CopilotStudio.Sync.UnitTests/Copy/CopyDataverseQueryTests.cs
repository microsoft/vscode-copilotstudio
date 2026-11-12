using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Agents.Platform.Content.Abstractions;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests.Copy;

public class CopyDataverseQueryTests
{
    [Fact]
    public async Task FindExistingWorkflowIds_BatchesDistinctIdentifiersAndReturnsThoseFound()
    {
        var identifiers = Enumerable.Range(0, 120).Select(_ => Guid.NewGuid()).ToList();
        var existing = identifiers.Where((_, index) => index % 3 == 0).ToHashSet();
        var (client, requests) = Create(request => Rows("workflowid", Matched(request, "workflowid").Where(existing.Contains)));

        var found = await client.FindExistingWorkflowIdsAsync(identifiers.Concat(identifiers.Take(10)).Append(Guid.Empty), CancellationToken.None);

        Assert.Equal(3, requests.Count);
        Assert.All(requests, request => Assert.Contains("/api/data/v9.2/workflows?", request.Uri, StringComparison.Ordinal));
        Assert.Equal(120, requests.Sum(request => Regex.Matches(Uri.UnescapeDataString(request.Uri), "workflowid eq ").Count));
        Assert.Equal(existing.OrderBy(identifier => identifier), found.OrderBy(identifier => identifier));
    }

    [Fact]
    public async Task FindExistingIds_QueryTheirOwnEntitySets()
    {
        var model = Guid.NewGuid();
        var connector = Guid.NewGuid();
        var (client, requests) = Create(request => request.RequestUri?.AbsolutePath.EndsWith("msdyn_aimodels", StringComparison.Ordinal) == true ? Rows("msdyn_aimodelid", new[] { model }) : Rows("connectorid", new[] { connector }));

        Assert.Equal(new[] { model }, await client.FindExistingAIModelIdsAsync(new[] { model }, CancellationToken.None));
        Assert.Equal(new[] { connector }, await client.FindExistingConnectorIdsAsync(new[] { connector }, CancellationToken.None));

        Assert.Contains("msdyn_aimodelid eq " + model.ToString("D"), Uri.UnescapeDataString(requests[0].Uri), StringComparison.Ordinal);
        Assert.Contains("connectorid eq " + connector.ToString("D"), Uri.UnescapeDataString(requests[1].Uri), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindExistingIds_WithNothingToLookUp_SendsNothing()
    {
        var (client, requests) = Create(_ => Json(HttpStatusCode.OK, "{\"value\":[]}"));

        Assert.Empty(await client.FindExistingWorkflowIdsAsync(new[] { Guid.Empty }, CancellationToken.None));
        Assert.Empty(requests);
    }

    [Fact]
    public async Task FindComponentSchemaNames_FiltersByPrefixAndLimitsTheRows()
    {
        var (client, requests) = Create(_ => Json(HttpStatusCode.OK, "{\"value\":[{\"schemaname\":\"cr1_copy.topic.A\"},{\"schemaname\":\"\"}]}"));

        var names = await client.FindComponentSchemaNamesAsync("cr1_copy.", 5, CancellationToken.None);

        Assert.Equal(new[] { "cr1_copy.topic.A" }, names);
        var uri = Uri.UnescapeDataString(Assert.Single(requests).Uri);
        Assert.Contains("startswith(schemaname,'cr1[_]copy.')", uri, StringComparison.Ordinal);
        Assert.Contains("$top=5", uri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindConnectorEnvironmentSuffix_SamplesUnmanagedCustomConnectorsOncePerEnvironment()
    {
        var (client, requests) = Create(_ => Connectors(("cr1_5Fweather-20plus", "Weather Plus", "shared_cr1-5fweather-20plus-5fd6c23f2bd6bf7828"), ("cr2_maps", "Maps", "shared_cr2-5fmaps-5fd6c23f2bd6bf7828"), ("cr3_custom", "Custom", "shared_somethingelse-5fc57fa9bfea132312")));

        Assert.Equal("d6c23f2bd6bf7828", await client.FindConnectorEnvironmentSuffixAsync(CancellationToken.None));
        Assert.Equal("d6c23f2bd6bf7828", await client.FindConnectorEnvironmentSuffixAsync(CancellationToken.None));
        client.SetDataverseUrl("https://other.crm.dynamics.com");
        Assert.Equal("d6c23f2bd6bf7828", await client.FindConnectorEnvironmentSuffixAsync(CancellationToken.None));

        Assert.Equal(2, requests.Count);
        Assert.Equal("https://test.crm.dynamics.com/api/data/v9.2/connectors?$select=name,displayname,connectorinternalid&$filter=ismanaged eq false and startswith(connectorinternalid,'shared[_]')&$top=50", Uri.UnescapeDataString(requests[0].Uri));
        Assert.StartsWith("https://other.crm.dynamics.com/", requests[1].Uri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindConnectorEnvironmentSuffix_RowsThatDisagree_AreUnknownAndReadAgain()
    {
        var (client, requests) = Create(_ => Connectors(("cr2_maps", "Maps", "shared_cr2-5fmaps-5fd6c23f2bd6bf7828"), ("cr1_5Fweather-20plus", "Weather Plus", "shared_cr1-5fweather-20plus-5fc57fa9bfea132312")));

        Assert.Null(await client.FindConnectorEnvironmentSuffixAsync(CancellationToken.None));
        Assert.Null(await client.FindConnectorEnvironmentSuffixAsync(CancellationToken.None));

        Assert.Equal(2, requests.Count);
    }

    [Fact]
    public async Task FindConnectorEnvironmentSuffix_WithoutAConformingRow_IsUnknown()
    {
        var (client, _) = Create(_ => Connectors(("cr1_5Fweather-20plus", "Weather Plus", "shared_weatherplus-5fd6c23f2bd6bf7828")));

        Assert.Null(await client.FindConnectorEnvironmentSuffixAsync(CancellationToken.None));
    }

    [Fact]
    public async Task FindConnectorsByInternalIdPrefixes_BatchesDistinctPrefixesIntoStartsWithFilters()
    {
        var prefixes = Enumerable.Range(0, 60).Select(index => $"shared_cr1-5fweather{index}-5f").ToList();
        var connectorId = Guid.NewGuid();
        var (client, requests) = Create(_ => Json(HttpStatusCode.OK, $"{{\"value\":[{{\"connectorid\":\"{connectorId}\",\"connectorinternalid\":\"shared_cr1-5fweather0-5fd6c23f2bd6bf7828\"}}]}}"));

        var found = await client.FindConnectorsByInternalIdPrefixesAsync(prefixes.Concat(prefixes.Take(5).Select(prefix => prefix.ToUpperInvariant())).Append(" "), CancellationToken.None);

        Assert.Equal(connectorId, Assert.Single(found).ConnectorId);
        Assert.Equal(2, requests.Count);
        Assert.Equal(60, requests.Sum(request => Regex.Matches(Uri.UnescapeDataString(request.Uri), @"startswith\(connectorinternalid,'").Count));
        Assert.Contains("$filter=(startswith(connectorinternalid,'shared[_]cr1-5fweather0-5f') or startswith(connectorinternalid,'shared[_]cr1-5fweather1-5f') or ", Uri.UnescapeDataString(requests[0].Uri), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetConnectorsByInternalIdPrefix_QueriesOnePrefixAndNothingForABlankOne()
    {
        var (client, requests) = Create(_ => Json(HttpStatusCode.OK, "{\"value\":[]}"));

        Assert.Empty(await client.GetConnectorsByInternalIdPrefixAsync(" ", CancellationToken.None));
        Assert.Empty(requests);
        Assert.Empty(await client.GetConnectorsByInternalIdPrefixAsync("shared_o'brien-5f", CancellationToken.None));

        Assert.Equal("https://test.crm.dynamics.com/api/data/v9.2/connectors?$select=connectorid,name,displayname,connectorinternalid,connectortype,ismanaged,modifiedon&$filter=(startswith(connectorinternalid,'shared[_]o''brien-5f'))", Uri.UnescapeDataString(Assert.Single(requests).Uri));
    }

    [Fact]
    public async Task FindCloudFlowsNamed_QueriesUnmanagedCloudFlowsByExactNameOnce()
    {
        var workflowId = Guid.NewGuid();
        var (client, requests) = Create(_ => Json(HttpStatusCode.OK, $"{{\"value\":[{{\"workflowid\":\"{workflowId}\",\"name\":\"o'brien's 50%_off approvals\"}}]}}"));

        var flow = Assert.Single(await client.FindCloudFlowsNamedAsync("O'Brien's 50%_off approvals", CancellationToken.None));
        Assert.Equal(workflowId, flow.WorkflowId);
        Assert.Equal("o'brien's 50%_off approvals", flow.Name);
        Assert.Equal("https://test.crm.dynamics.com/api/data/v9.2/workflows?$select=workflowid,name,description,statecode,statuscode,modifiedon&$filter=category eq 5 and type eq 1 and ismanaged eq false and name eq 'O''Brien''s 50%_off approvals'&$top=1", Uri.UnescapeDataString(Assert.Single(requests).Uri));
    }

    [Theory]
    [InlineData(false, " and ismanaged eq false")]
    [InlineData(true, "")]
    public async Task GetConnectorVersionsByInternalIds_BatchesFollowsPagesAndKeepsEachConnectorOnce(bool isManaged, string scopeFilter)
    {
        var identifiers = Enumerable.Range(0, 55).Select(index => $"shared_connector{index}-5fd6c23f2bd6bf7828").ToList();
        var connectorId = Guid.NewGuid();
        var (client, requests) = Create(request => request.RequestUri?.Query.Contains("page2", StringComparison.Ordinal) == true
            ? Json(HttpStatusCode.OK, $"{{\"value\":[{{\"connectorid\":\"{connectorId}\",\"connectorinternalid\":\"shared_connector0-5fd6c23f2bd6bf7828\"}}]}}")
            : Json(HttpStatusCode.OK, $"{{\"value\":[{{\"connectorid\":\"{connectorId}\",\"connectorinternalid\":\"shared_connector0-5fd6c23f2bd6bf7828\"}}],\"@odata.nextLink\":\"https://test.crm.dynamics.com/api/data/v9.2/connectors?page2=1\"}}"));

        var found = await client.GetConnectorVersionsByInternalIdsAsync(identifiers.Append(identifiers[0]), isManaged, CancellationToken.None);

        Assert.Equal(connectorId, Assert.Single(found).ConnectorId);
        Assert.Equal(4, requests.Count);
        Assert.EndsWith($"connectorinternalid eq 'shared_connector49-5fd6c23f2bd6bf7828'){scopeFilter}", Uri.UnescapeDataString(requests[0].Uri), StringComparison.Ordinal);
        Assert.Contains("$select=connectorid,connectorinternalid,ismanaged,modifiedon,versionnumber&", Uri.UnescapeDataString(requests[0].Uri), StringComparison.Ordinal);
    }

    private static HttpResponseMessage Connectors(params (string Name, string DisplayName, string InternalId)[] rows) => Json(HttpStatusCode.OK, "{\"value\":[" + string.Join(",", rows.Select(row => $"{{\"name\":\"{row.Name}\",\"displayname\":\"{row.DisplayName}\",\"connectorinternalid\":\"{row.InternalId}\"}}")) + "]}");

    private static IEnumerable<Guid> Matched(HttpRequestMessage request, string column) => Regex.Matches(Uri.UnescapeDataString(request.RequestUri?.Query ?? string.Empty), column + @" eq ([0-9a-fA-F-]{36})").Select(match => Guid.Parse(match.Groups[1].Value));

    private static HttpResponseMessage Rows(string column, IEnumerable<Guid> identifiers) => Json(HttpStatusCode.OK, "{\"value\":[" + string.Join(",", identifiers.Select(identifier => $"{{\"{column}\":\"{identifier}\"}}")) + "]}");

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static (SyncDataverseClient Client, List<RecordedRequest> Requests) Create(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var requests = new List<RecordedRequest>();
        var accessor = new Mock<IDataverseHttpClientAccessor>();
        accessor.Setup(candidate => candidate.CreateClient()).Returns(new HttpClient(new RecordingHandler(requests, respond)));
        var client = new SyncDataverseClient(accessor.Object);
        client.SetDataverseUrl("https://test.crm.dynamics.com");

        return (client, requests);
    }

    private sealed record RecordedRequest(string Uri);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly List<RecordedRequest> _requests;
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public RecordingHandler(List<RecordedRequest> requests, Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            _requests = requests;
            _respond = respond;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _requests.Add(new RecordedRequest($"{request.RequestUri}"));

            return Task.FromResult(_respond(request));
        }
    }
}
