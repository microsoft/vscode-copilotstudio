// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.Platform.Content.Abstractions;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;
using static Microsoft.CopilotStudio.Sync.Dataverse.SyncDataverseClient;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class StandaloneWorkflowDataverseTests
{
    private const string DataverseUrl = "https://test.crm.dynamics.com";

    private sealed record Exchange(HttpMethod Method, string Url, string Body, string? ClientRequestId);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        public List<Exchange> Exchanges { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Exchanges.Add(new Exchange(
                request.Method,
                request.RequestUri!.ToString(),
                request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(),
                request.Headers.TryGetValues("x-ms-client-request-id", out var clientRequestIds) ? string.Join(",", clientRequestIds) : null));

            return _respond(request);
        }
    }

    private const string NoActiveTransactionBody = "{\"error\":{\"code\":\"0x80040251\",\"message\":\"There is no active transaction. This error is usually caused by custom plug-ins that ignore errors from service calls and continue processing.\"}}";

    private static HttpResponseMessage Json(string payload) =>
        new(HttpStatusCode.OK) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Failure(HttpStatusCode statusCode, string payload) =>
        new(statusCode) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage NotFound() =>
        new(HttpStatusCode.NotFound) { Content = new StringContent("{\"error\":\"not found\"}", Encoding.UTF8, "application/json") };

    private static (SyncDataverseClient Client, RecordingHandler Handler) Create(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new RecordingHandler(respond);
        var accessor = new Mock<IDataverseHttpClientAccessor>();
        accessor.Setup(a => a.CreateClient()).Returns(new HttpClient(handler));

        var client = new SyncDataverseClient(accessor.Object, "CopilotStudio.Sync", new DataverseRetryPolicy { Delay = (_, _) => Task.CompletedTask });
        client.SetDataverseUrl(DataverseUrl);

        return (client, handler);
    }

    [Fact]
    public async Task ListWorkflowsAsync_FollowsAContinuationInsideTheEnvironment()
    {
        var pages = new Queue<string>(new[]
        {
            $"{{\"value\":[{{\"workflowid\":\"{Guid.NewGuid()}\",\"name\":\"First\"}}],\"@odata.nextLink\":\"{DataverseUrl}/api/data/v9.2/workflows?$skiptoken=next\"}}",
            $"{{\"value\":[{{\"workflowid\":\"{Guid.NewGuid()}\",\"name\":\"Second\"}}]}}",
        });

        var (client, handler) = Create(_ => Json(pages.Dequeue()));

        var summaries = await client.ListWorkflowsAsync(null, null, CancellationToken.None);

        Assert.Equal(2, summaries.Length);
        Assert.Equal(2, handler.Exchanges.Count);
    }

    [Theory]
    [InlineData("https://attacker.example.com/api/data/v9.2/workflows?$skiptoken=next")]
    [InlineData("https://test.crm.dynamics.com.attacker.example.com/api/data/v9.2/workflows")]
    [InlineData("http://test.crm.dynamics.com/api/data/v9.2/workflows")]
    [InlineData("https://test.crm.dynamics.com:8443/api/data/v9.2/workflows")]
    [InlineData("/api/data/v9.2/workflows?$skiptoken=next")]
    public async Task ListWorkflowsAsync_RefusesAContinuationOutsideTheEnvironment(string nextLink)
    {
        var pages = new Queue<string>(new[]
        {
            $"{{\"value\":[{{\"workflowid\":\"{Guid.NewGuid()}\",\"name\":\"First\"}}],\"@odata.nextLink\":\"{nextLink}\"}}",
            "{\"value\":[]}",
        });

        var (client, handler) = Create(_ => Json(pages.Dequeue()));

        await Assert.ThrowsAsync<DataverseRequestException>(() => client.ListWorkflowsAsync(null, null, CancellationToken.None));

        Assert.Single(handler.Exchanges);
    }

    [Fact]
    public async Task ARefusedContinuation_NeverReachesTheOtherOrigin()
    {
        var pages = new Queue<string>(new[]
        {
            $"{{\"value\":[{{\"workflowid\":\"{Guid.NewGuid()}\",\"name\":\"First\"}}],\"@odata.nextLink\":\"https://attacker.example.com/steal\"}}",
            "{\"value\":[]}",
        });

        var (client, handler) = Create(_ => Json(pages.Dequeue()));

        await Assert.ThrowsAsync<DataverseRequestException>(() => client.ListWorkflowsAsync(null, null, CancellationToken.None));

        Assert.DoesNotContain(handler.Exchanges, exchange => exchange.Url.Contains("attacker.example.com", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AContinuationDifferingOnlyInHostCasing_IsStillFollowed()
    {
        var pages = new Queue<string>(new[]
        {
            $"{{\"value\":[{{\"workflowid\":\"{Guid.NewGuid()}\",\"name\":\"First\"}}],\"@odata.nextLink\":\"https://TEST.CRM.DYNAMICS.COM/api/data/v9.2/workflows?$skiptoken=next\"}}",
            "{\"value\":[]}",
        });

        var (client, handler) = Create(_ => Json(pages.Dequeue()));

        await client.ListWorkflowsAsync(null, null, CancellationToken.None);

        Assert.Equal(2, handler.Exchanges.Count);
    }

    [Fact]
    public async Task ListWorkflowsAsync_WhenTheServiceStaysUnavailable_ReportsTheLastFailureAfterThreeAttempts()
    {
        var (client, handler) = Create(_ => Failure(HttpStatusCode.ServiceUnavailable, "{\"error\":{\"message\":\"Try again later.\"}}"));

        var failure = await Assert.ThrowsAsync<DataverseRequestException>(() => client.ListWorkflowsAsync(null, null, CancellationToken.None));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, failure.StatusCode);
        Assert.Contains("Try again later.", failure.ResponseBody, StringComparison.Ordinal);
        Assert.Equal(3, handler.Exchanges.Count);
        Assert.Single(handler.Exchanges.Select(exchange => exchange.ClientRequestId).Distinct());
    }

    [Fact]
    public async Task ListWorkflowsAsync_WhenTheServiceFailsWithoutATransientCode_ReportsTheFailureAtOnce()
    {
        var (client, handler) = Create(_ => Failure(HttpStatusCode.InternalServerError, "{\"error\":{\"code\":\"0x80040216\",\"message\":\"An unexpected error occurred.\"}}"));

        var failure = await Assert.ThrowsAsync<DataverseRequestException>(() => client.ListWorkflowsAsync(null, null, CancellationToken.None));

        Assert.Equal(HttpStatusCode.InternalServerError, failure.StatusCode);
        Assert.Contains("0x80040216", failure.ResponseBody, StringComparison.Ordinal);
        Assert.Single(handler.Exchanges);
    }

    [Fact]
    public async Task InsertWorkflowAsync_CreatesTheWorkflow()
    {
        var workflowId = Guid.NewGuid();
        var (client, handler) = Create(_ => Json("{}"));

        var response = await client.InsertWorkflowAsync(new WorkflowMetadata { WorkflowId = workflowId, Name = "Standalone", ClientData = "{}", StateCode = 0, StatusCode = 1 }, CancellationToken.None);

        var post = Assert.Single(handler.Exchanges, exchange => exchange.Method == HttpMethod.Post);
        Assert.Contains("/api/data/v9.2/workflows", post.Url, StringComparison.Ordinal);
        Assert.Contains(workflowId.ToString(), post.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(string.Empty, response.ErrorMessage);
    }

    [Fact]
    public async Task InsertWorkflowAsync_WhenTheMetadataSaysActivated_ActivatesAfterCreating()
    {
        var (client, handler) = Create(_ => Json("{}"));

        await client.InsertWorkflowAsync(new WorkflowMetadata { WorkflowId = Guid.NewGuid(), Name = "Standalone", ClientData = "{}", StateCode = 1, StatusCode = 2 }, CancellationToken.None);

        var activation = Assert.Single(handler.Exchanges, exchange => exchange.Method.Method == "PATCH");
        Assert.Contains("\"statecode\":1", activation.Body.Replace(" ", string.Empty), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InsertWorkflowAsync_WhenTheServiceIsUnavailable_DoesNotCreateTheWorkflowTwice()
    {
        var (client, handler) = Create(_ => Failure(HttpStatusCode.ServiceUnavailable, "{\"error\":{\"message\":\"Try again later.\"}}"));

        var response = await client.InsertWorkflowAsync(new WorkflowMetadata { WorkflowId = Guid.NewGuid(), Name = "Standalone", ClientData = "{}", StateCode = 0, StatusCode = 1 }, CancellationToken.None);

        Assert.Contains("Try again later.", response.ErrorMessage, StringComparison.Ordinal);
        Assert.Single(handler.Exchanges);
    }

    [Fact]
    public async Task UpdateWorkflowAsync_WhenTheUpdateIsBrieflyUnavailable_SendsTheSameUpdateAgainUnderOneRequestId()
    {
        var workflowId = Guid.NewGuid();
        var updates = 0;
        var (client, handler) = Create(request => request.Method == HttpMethod.Get
            ? Json($"{{\"workflowid\":\"{workflowId}\"}}")
            : ++updates == 1
                ? Failure(HttpStatusCode.ServiceUnavailable, string.Empty)
                : Json($"{{\"workflowid\":\"{workflowId}\",\"name\":\"Standalone\"}}"));

        var response = await client.UpdateWorkflowAsync(null, new WorkflowMetadata { WorkflowId = workflowId, Name = "Standalone", ClientData = "{}" }, CancellationToken.None);

        Assert.Equal(string.Empty, response.ErrorMessage);
        Assert.Equal("Standalone", response.StoredWorkflow?.Name);
        var sent = handler.Exchanges.Where(exchange => exchange.Method.Method == "PATCH").ToArray();
        Assert.Equal(2, sent.Length);
        Assert.Contains("\"name\":\"Standalone\"", sent[0].Body.Replace(" ", string.Empty), StringComparison.Ordinal);
        Assert.Equal(sent[0].Body, sent[1].Body);
        Assert.Equal(sent[0].ClientRequestId, sent[1].ClientRequestId);
        Assert.NotEqual(handler.Exchanges[0].ClientRequestId, sent[0].ClientRequestId);
    }

    [Fact]
    public async Task UpdateWorkflowAsync_WhenTheWorkflowIsAbsentAndAnAgentOwnsIt_InsertsIt()
    {
        var workflowId = Guid.NewGuid();
        var (client, handler) = Create(request => request.Method == HttpMethod.Get ? NotFound() : Json("{}"));

        var response = await client.UpdateWorkflowAsync(Guid.NewGuid(), new WorkflowMetadata { WorkflowId = workflowId, Name = "Standalone", ClientData = "{}", StateCode = 0, StatusCode = 1 }, CancellationToken.None);

        var post = Assert.Single(handler.Exchanges, exchange => exchange.Method == HttpMethod.Post);
        Assert.Contains(workflowId.ToString(), post.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(string.Empty, response.ErrorMessage);
    }

    [Fact]
    public async Task UpdateWorkflowAsync_WhenTheExistenceCheckFails_ReportsTheFailureRatherThanClaimingSuccess()
    {
        var (client, handler) = Create(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("boom", Encoding.UTF8, "application/json") });

        var response = await client.UpdateWorkflowAsync(null, new WorkflowMetadata { WorkflowId = Guid.NewGuid(), Name = "Standalone", ClientData = "{}" }, CancellationToken.None);

        Assert.NotEmpty(response.ErrorMessage);
        Assert.DoesNotContain(handler.Exchanges, exchange => exchange.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task UpdateWorkflowAsync_WhenTheRowIsMissingAndNoAgentOwnsIt_ReportsTheFailureInsteadOfInserting()
    {
        var (client, handler) = Create(request => request.Method == HttpMethod.Get ? NotFound() : Json("{}"));

        var response = await client.UpdateWorkflowAsync(null, new WorkflowMetadata { WorkflowId = Guid.NewGuid(), Name = "Orphan", ClientData = "{}" }, CancellationToken.None);

        Assert.DoesNotContain(handler.Exchanges, exchange => exchange.Method == HttpMethod.Post);
        Assert.NotEmpty(response.ErrorMessage);
    }

    [Fact]
    public async Task UpdateWorkflowAsync_WhenTheRowIsMissingAndAnAgentOwnsIt_InsertsThroughTheAgentOverload()
    {
        var (client, handler) = Create(request => request.Method == HttpMethod.Get ? NotFound() : Json("{}"));

        await client.UpdateWorkflowAsync(Guid.NewGuid(), new WorkflowMetadata { WorkflowId = Guid.NewGuid(), Name = "Owned", ClientData = "{}" }, CancellationToken.None);

        Assert.Contains(handler.Exchanges, exchange => exchange.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task UpdateWorkflowAsync_WhenTheRowIsMissingAndTheAgentIdIsEmpty_DoesNotInsert()
    {
        var (client, handler) = Create(request => request.Method == HttpMethod.Get ? NotFound() : Json("{}"));

        var response = await client.UpdateWorkflowAsync(Guid.Empty, new WorkflowMetadata { WorkflowId = Guid.NewGuid(), Name = "Owned", ClientData = "{}" }, CancellationToken.None);

        Assert.NotEmpty(response.ErrorMessage);
        Assert.DoesNotContain(handler.Exchanges, exchange => exchange.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task UpdateWorkflowAsync_WhenTheRowIsMissingAndAnAgentOwnsIt_Inserts()
    {
        var (client, handler) = Create(request => request.Method == HttpMethod.Get ? NotFound() : Json("{}"));

        await client.UpdateWorkflowAsync(Guid.NewGuid(), new WorkflowMetadata { WorkflowId = Guid.NewGuid(), Name = "Owned", ClientData = "{}" }, CancellationToken.None);

        Assert.Contains(handler.Exchanges, exchange => exchange.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task InsertWorkflowAsync_WhenAnAgentIdIsRequiredButMissing_Throws()
    {
        var (client, _) = Create(_ => Json("{}"));

        await Assert.ThrowsAsync<ArgumentNullException>(() => client.InsertWorkflowAsync(Guid.Empty, new WorkflowMetadata { WorkflowId = Guid.NewGuid(), ClientData = "{}" }, CancellationToken.None));
    }

    [Fact]
    public async Task InsertWorkflowAsync_WhenTheWriteFails_LeavesTheCallersStateAlone()
    {
        var (client, _) = Create(request => request.Method == HttpMethod.Post ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("boom") } : Json("{}"));
        var workflow = new WorkflowMetadata { WorkflowId = Guid.NewGuid(), Name = "Running", ClientData = "{}", StateCode = 1, StatusCode = 2 };

        var response = await client.InsertWorkflowAsync(workflow, CancellationToken.None);

        Assert.NotEmpty(response.ErrorMessage);
        Assert.True(response.IsDisabled);
        Assert.Equal(1, workflow.StateCode);
        Assert.Equal(2, workflow.StatusCode);
    }

    [Fact]
    public async Task InsertWorkflowAsync_WhenActivationIsRejected_DoesNotReportARunningWorkflow()
    {
        var (client, _) = Create(request => request.Method == HttpMethod.Post
            ? Json("{}")
            : new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("ConnectionAuthorizationFailed") });

        var workflow = new WorkflowMetadata { WorkflowId = Guid.NewGuid(), Name = "Running", ClientData = "{}", StateCode = 1, StatusCode = 2 };

        var response = await client.InsertWorkflowAsync(workflow, CancellationToken.None);

        Assert.NotEmpty(response.ErrorMessage);
        Assert.True(response.IsDisabled);
    }

    [Fact]
    public async Task InsertWorkflowAsync_WhenActivationSucceeds_ReportsARunningWorkflow()
    {
        var (client, _) = Create(_ => Json("{}"));
        var workflow = new WorkflowMetadata { WorkflowId = Guid.NewGuid(), Name = "Running", ClientData = "{}", StateCode = 1, StatusCode = 2 };

        var response = await client.InsertWorkflowAsync(workflow, CancellationToken.None);

        Assert.Empty(response.ErrorMessage);
        Assert.False(response.IsDisabled);
        Assert.Equal(1, workflow.StateCode);
        Assert.Equal(2, workflow.StatusCode);
    }

    [Fact]
    public async Task InsertWorkflowAsync_WhenTheCallerAsksForADraft_ReportsADisabledWorkflow()
    {
        var (client, _) = Create(_ => Json("{}"));
        var workflow = new WorkflowMetadata { WorkflowId = Guid.NewGuid(), Name = "Draft", ClientData = "{}", StateCode = 0, StatusCode = 1 };

        var response = await client.InsertWorkflowAsync(workflow, CancellationToken.None);

        Assert.Empty(response.ErrorMessage);
        Assert.True(response.IsDisabled);
    }

    [Fact]
    public async Task InsertWorkflowAsync_DoesNotSendStateColumns()
    {
        var (client, handler) = Create(_ => Json("{}"));

        await client.InsertWorkflowAsync(new WorkflowMetadata { WorkflowId = Guid.NewGuid(), Name = "Draft", ClientData = "{}", StateCode = 0, StatusCode = 1 }, CancellationToken.None);

        var post = handler.Exchanges.Single(exchange => exchange.Method == HttpMethod.Post);
        Assert.DoesNotContain("statecode", post.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("statuscode", post.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetWorkflowAsync_WhenNoRowMatches_ReturnsNull()
    {
        var (client, _) = Create(_ => Json("{\"value\":[]}"));

        Assert.Null(await client.GetWorkflowAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task GetWorkflowAsync_ReturnsTheWorkflowWithItsDefinition()
    {
        var workflowId = Guid.NewGuid();
        var (client, handler) = Create(_ => Json($"{{\"value\":[{{\"workflowid\":\"{workflowId}\",\"name\":\"Flow\",\"clientdata\":\"{{}}\"}}]}}"));

        var workflow = await client.GetWorkflowAsync(workflowId, CancellationToken.None);

        Assert.NotNull(workflow);
        Assert.Equal(workflowId, workflow!.WorkflowId);
        Assert.Equal("Flow", workflow.Name);
        Assert.Contains($"workflowid eq {workflowId}", Uri.UnescapeDataString(handler.Exchanges[0].Url), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetWorkflowAsync_ReadsWhetherTheFlowIsManagedInTheSameRequest()
    {
        var workflowId = Guid.NewGuid();
        var (client, handler) = Create(_ => Json($"{{\"value\":[{{\"workflowid\":\"{workflowId}\",\"name\":\"Flow\",\"clientdata\":\"{{}}\",\"ismanaged\":true}}]}}"));

        var workflow = await client.GetWorkflowAsync(workflowId, CancellationToken.None);

        Assert.True(workflow!.IsManaged);
        Assert.Contains("ismanaged", Assert.Single(handler.Exchanges).Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetWorkflowAsync_DerivesTheConnectionReferencesTheDefinitionDeclares()
    {
        var workflowId = Guid.NewGuid();
        var clientData = JsonSerializer.Serialize("""{"properties":{"connectionReferences":{"shared_teams":{"api":{"name":"shared_teams"},"connection":{"connectionReferenceLogicalName":"new_teams"}}}}}""");
        var (client, _) = Create(_ => Json($"{{\"value\":[{{\"workflowid\":\"{workflowId}\",\"name\":\"Flow\",\"clientdata\":{clientData}}}]}}"));

        var workflow = await client.GetWorkflowAsync(workflowId, CancellationToken.None);

        Assert.Equal(["new_teams"], workflow!.ConnectionReferences);
    }

    [Fact]
    public async Task GetWorkflowAsync_WhenTheDefinitionDeclaresNothing_LeavesTheReferencesEmpty()
    {
        var workflowId = Guid.NewGuid();
        var (client, _) = Create(_ => Json($"{{\"value\":[{{\"workflowid\":\"{workflowId}\",\"name\":\"Flow\",\"clientdata\":\"{{}}\"}}]}}"));

        Assert.Empty((await client.GetWorkflowAsync(workflowId, CancellationToken.None))!.ConnectionReferences);
    }

    [Fact]
    public async Task GetWorkflowAsync_FeedsThePlannerEnoughToDraftAnUnboundWorkflow()
    {
        var workflowId = Guid.NewGuid();
        var clientData = JsonSerializer.Serialize("""{"properties":{"connectionReferences":{"shared_teams":{"api":{"name":"shared_teams"},"connection":{"connectionReferenceLogicalName":"new_teams"}}}}}""");
        var (client, _) = Create(_ => Json($"{{\"value\":[{{\"workflowid\":\"{workflowId}\",\"name\":\"Flow\",\"clientdata\":{clientData},\"statecode\":1,\"statuscode\":2}}]}}"));

        var workflow = await client.GetWorkflowAsync(workflowId, CancellationToken.None);

        var unbound = new Mock<IStandaloneWorkflowDataverseClient>();
        unbound
            .Setup(c => c.GetConnectionReferencesByLogicalNamesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ConnectionReferenceInfo { ConnectionReferenceLogicalName = "new_teams", ConnectorId = "shared_teams", ConnectionId = string.Empty }]);

        Assert.Single(await WorkflowActivationPlanner.DraftStandaloneUnboundActivationsAsync([workflow!], unbound.Object, CancellationToken.None));
        Assert.Equal(0, workflow!.StateCode);
    }

    [Fact]
    public async Task ListWorkflowsAsync_RequestsOnlyCloudFlows()
    {
        var (client, handler) = Create(_ => Json("{\"value\":[]}"));

        await client.ListWorkflowsAsync(null, null, CancellationToken.None);

        Assert.Contains("category eq 5 and type eq 1", Uri.UnescapeDataString(handler.Exchanges[0].Url), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListWorkflowsAsync_NeverRequestsTheDefinition()
    {
        var (client, handler) = Create(_ => Json("{\"value\":[]}"));

        await client.ListWorkflowsAsync(null, null, CancellationToken.None);

        Assert.DoesNotContain("clientdata", handler.Exchanges[0].Url, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ListWorkflowsAsync_AppliesTheNameFilter()
    {
        var (client, handler) = Create(_ => Json("{\"value\":[]}"));

        await client.ListWorkflowsAsync("  Expense  ", null, CancellationToken.None);

        Assert.Contains("contains(name,'Expense')", Uri.UnescapeDataString(handler.Exchanges[0].Url), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListWorkflowsAsync_EscapesAQuoteInTheNameFilter()
    {
        var (client, handler) = Create(_ => Json("{\"value\":[]}"));

        await client.ListWorkflowsAsync("O'Brien", null, CancellationToken.None);

        Assert.Contains("contains(name,'O''Brien')", Uri.UnescapeDataString(handler.Exchanges[0].Url), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListWorkflowsAsync_MatchesWildcardCharactersInTheNameFilterLiterally()
    {
        var (client, handler) = Create(_ => Json("{\"value\":[]}"));

        await client.ListWorkflowsAsync("50%_off", null, CancellationToken.None);

        Assert.Contains("contains(name,'50[%][_]off')", Uri.UnescapeDataString(handler.Exchanges[0].Url), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetConnectorsByInternalIdPrefixAsync_MatchesAnUnderscoreInThePrefixLiterally()
    {
        var (client, handler) = Create(_ => Json("{\"value\":[]}"));

        await client.GetConnectorsByInternalIdPrefixAsync("cr123_my", CancellationToken.None);

        Assert.Contains("startswith(connectorinternalid,'cr123[_]my')", Uri.UnescapeDataString(handler.Exchanges[0].Url), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetConnectorVersionsByIdsAsync_BatchesDistinctIdsAndSelectsOnlyVersionColumns()
    {
        var (client, handler) = Create(_ => Json("{\"value\":[]}"));
        var connectorIds = Enumerable.Range(0, 51).Select(_ => Guid.NewGuid()).ToList();

        await client.GetConnectorVersionsByIdsAsync(connectorIds.Concat(new[] { connectorIds[0], Guid.Empty }).ToList(), CancellationToken.None);

        Assert.Equal(2, handler.Exchanges.Count);
        Assert.All(handler.Exchanges, exchange =>
        {
            Assert.Equal(HttpMethod.Get, exchange.Method);
            Assert.Contains("/connectors?$select=connectorid,connectorinternalid,ismanaged,modifiedon,versionnumber&", Uri.UnescapeDataString(exchange.Url), StringComparison.Ordinal);
            Assert.DoesNotContain("ismanaged eq false", Uri.UnescapeDataString(exchange.Url), StringComparison.Ordinal);
            Assert.DoesNotContain(Guid.Empty.ToString(), exchange.Url, StringComparison.Ordinal);
        });
        Assert.All(connectorIds, connectorId => Assert.Single(handler.Exchanges, exchange => Uri.UnescapeDataString(exchange.Url).Contains($"connectorid eq {connectorId}", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task GetConnectorVersionsByIdsAsync_NoIds_SendsNoRequest()
    {
        var (client, handler) = Create(_ => Json("{\"value\":[]}"));

        var connectors = await client.GetConnectorVersionsByIdsAsync(Array.Empty<Guid>(), CancellationToken.None);

        Assert.Empty(connectors);
        Assert.Empty(handler.Exchanges);
    }

    [Fact]
    public async Task SaveConnectorAsync_NewConnector_PostsCreateOnlyFieldsAndReturnsTheStoredConnector()
    {
        var connectorId = Guid.NewGuid();
        var preferences = new List<string>();
        var (client, handler) = Create(request =>
        {
            preferences.Add(string.Join(",", request.Headers.TryGetValues("Prefer", out var values) ? values : Array.Empty<string>()));

            return Json($"{{\"connectorid\":\"{connectorId}\",\"name\":\"shared_contoso\",\"description\":\"Stored by the server\",\"versionnumber\":42}}");
        });

        var stored = await client.SaveConnectorAsync(
            new CustomConnectorMetadata { ConnectorId = connectorId, Name = "shared_contoso", ConnectorInternalId = "shared_contoso-5f", ConnectorType = 1, Description = "Sent by the client" },
            exists: false,
            CancellationToken.None);

        var exchange = Assert.Single(handler.Exchanges);
        Assert.Equal(HttpMethod.Post, exchange.Method);
        Assert.Equal($"{DataverseUrl}/api/data/v9.2/connectors?$select=connectorid,name,displayname,description,connectorinternalid,openapidefinition,connectionparameters,connectionparametersets,policytemplateinstances,iconbrandcolor,iconblob,connectortype,statecode,statuscode,ismanaged,componentstate,createdon,modifiedon,versionnumber", Uri.UnescapeDataString(exchange.Url));
        Assert.Equal("return=representation", Assert.Single(preferences));
        using var body = JsonDocument.Parse(exchange.Body);
        Assert.Equal(connectorId, body.RootElement.GetProperty("connectorid").GetGuid());
        Assert.Equal("shared_contoso-5f", body.RootElement.GetProperty("connectorinternalid").GetString());
        Assert.Equal(1, body.RootElement.GetProperty("connectortype").GetInt32());
        Assert.False(body.RootElement.TryGetProperty("openapidefinition", out _));
        Assert.Equal("Stored by the server", stored.Description);
        Assert.Equal(42, stored.VersionNumber);
    }

    [Fact]
    public async Task SaveConnectorAsync_ExistingConnector_PatchesWithoutCreateOnlyFields()
    {
        var connectorId = Guid.NewGuid();
        var (client, handler) = Create(_ => Json($"{{\"connectorid\":\"{connectorId}\",\"versionnumber\":7}}"));

        var stored = await client.SaveConnectorAsync(
            new CustomConnectorMetadata { ConnectorId = connectorId, Name = "shared_contoso", ConnectorInternalId = "shared_contoso-5f", ConnectorType = 1, IconBrandColor = "#007ee5" },
            exists: true,
            CancellationToken.None);

        var exchange = Assert.Single(handler.Exchanges);
        Assert.Equal("PATCH", exchange.Method.Method);
        Assert.StartsWith($"{DataverseUrl}/api/data/v9.2/connectors({connectorId})?$select=connectorid,", Uri.UnescapeDataString(exchange.Url), StringComparison.Ordinal);
        using var body = JsonDocument.Parse(exchange.Body);
        Assert.Equal("#007ee5", body.RootElement.GetProperty("iconbrandcolor").GetString());
        Assert.False(body.RootElement.TryGetProperty("connectorid", out _));
        Assert.False(body.RootElement.TryGetProperty("connectorinternalid", out _));
        Assert.False(body.RootElement.TryGetProperty("connectortype", out _));
        Assert.Equal(7, stored.VersionNumber);
    }

    [Fact]
    public async Task SaveConnectorAsync_EmptyResponse_ReadsTheStoredConnectorOnce()
    {
        var connectorId = Guid.NewGuid();
        var (client, handler) = Create(request => request.Method == HttpMethod.Get
            ? Json($"{{\"connectorid\":\"{connectorId}\",\"description\":\"Read back\",\"versionnumber\":9}}")
            : new HttpResponseMessage(HttpStatusCode.NoContent));

        var stored = await client.SaveConnectorAsync(new CustomConnectorMetadata { ConnectorId = connectorId, Name = "shared_contoso" }, exists: true, CancellationToken.None);

        Assert.Equal(2, handler.Exchanges.Count);
        Assert.Equal(HttpMethod.Get, handler.Exchanges[1].Method);
        Assert.StartsWith($"{DataverseUrl}/api/data/v9.2/connectors({connectorId})?$select=connectorid,", Uri.UnescapeDataString(handler.Exchanges[1].Url), StringComparison.Ordinal);
        Assert.Equal("Read back", stored.Description);
        Assert.Equal(9, stored.VersionNumber);
    }

    [Fact]
    public async Task ListWorkflowsAsync_ListsEveryUnmanagedCloudFlowIncludingTheOnesAgentsUseInOneRequest()
    {
        var preferences = new List<bool>();
        var (client, handler) = Create(request =>
        {
            preferences.Add(request.Headers.Contains("Prefer"));

            return Json("{\"value\":[]}");
        });

        await client.ListWorkflowsAsync(null, 5, CancellationToken.None);

        var url = Uri.UnescapeDataString(Assert.Single(handler.Exchanges).Url);
        Assert.Contains("$filter=category eq 5 and type eq 1 and ismanaged eq false&", url, StringComparison.Ordinal);
        Assert.DoesNotContain("botcomponent_workflow", url, StringComparison.Ordinal);
        Assert.Contains("$top=5", url, StringComparison.Ordinal);
        Assert.DoesNotContain("$expand", url, StringComparison.Ordinal);
        Assert.Equal([false], preferences);
    }

    [Fact]
    public async Task GetAgentsUsingWorkflowAsync_ReadsTheAgentNamesInOneRequestThatAsksForFormattedValues()
    {
        var workflowId = Guid.NewGuid();
        var preferences = new List<string>();
        var (client, handler) = Create(request =>
        {
            preferences.AddRange(request.Headers.GetValues("Prefer"));

            return Json($$"""{"workflowid":"{{workflowId}}","botcomponent_workflow":[]}""");
        });

        await client.GetAgentsUsingWorkflowAsync(workflowId, CancellationToken.None);

        var exchange = Assert.Single(handler.Exchanges);
        Assert.Equal(HttpMethod.Get, exchange.Method);
        Assert.Equal($"{DataverseUrl}/api/data/v9.2/workflows({workflowId})?$select=workflowid&$expand=botcomponent_workflow($select=_parentbotid_value,_parentbotcomponentcollectionid_value)", Uri.UnescapeDataString(exchange.Url));
        Assert.Equal(["odata.include-annotations=\"OData.Community.Display.V1.FormattedValue\""], preferences);
    }

    [Fact]
    public async Task GetAgentsUsingWorkflowAsync_NamesEachAgentOnceInOrderAndFallsBackToTheComponentCollection()
    {
        const string Agent = "_parentbotid_value@OData.Community.Display.V1.FormattedValue";
        const string Collection = "_parentbotcomponentcollectionid_value@OData.Community.Display.V1.FormattedValue";
        var (client, _) = Create(_ => Json($$"""
            {"botcomponent_workflow":[
              {"{{Agent}}":"Zeta"},
              {"{{Agent}}":"Alpha"},
              {"{{Agent}}":"Alpha","{{Collection}}":"Ignored Collection"},
              {"{{Collection}}":"Collection One"},
              {"{{Agent}}":"  ","{{Collection}}":"Collection Two"},
              {"{{Agent}}":null,"{{Collection}}":"  "},
              {}
            ]}
            """));

        var names = await client.GetAgentsUsingWorkflowAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(["Alpha", "Collection One", "Collection Two", "Zeta"], names);
    }

    [Theory]
    [InlineData("{\"botcomponent_workflow\":[]}")]
    [InlineData("{\"workflowid\":\"00000000-0000-0000-0000-000000000001\"}")]
    public async Task GetAgentsUsingWorkflowAsync_WhenNoAgentUsesTheWorkflow_ReturnsNoNames(string payload)
    {
        var (client, _) = Create(_ => Json(payload));

        Assert.Empty(await client.GetAgentsUsingWorkflowAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task GetAgentsUsingWorkflowAsync_WhenTheWorkflowIsGone_ReturnsNoNames()
    {
        var (client, handler) = Create(_ => NotFound());

        Assert.Empty(await client.GetAgentsUsingWorkflowAsync(Guid.NewGuid(), CancellationToken.None));
        Assert.Single(handler.Exchanges);
    }

    [Fact]
    public async Task GetAgentsUsingWorkflowAsync_WhenTheServiceRefusesTheRead_ReportsTheFailure()
    {
        var (client, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{\"error\":\"denied\"}", Encoding.UTF8, "application/json") });

        var failure = await Assert.ThrowsAsync<DataverseRequestException>(() => client.GetAgentsUsingWorkflowAsync(Guid.NewGuid(), CancellationToken.None));

        Assert.Equal(HttpStatusCode.Forbidden, failure.StatusCode);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    public async Task HasUnpublishedDraftAsync_ReadsTheUnpublishedRowsComponentStateInOneRequest(int componentState, bool expected)
    {
        var workflowId = Guid.NewGuid();
        var (client, handler) = Create(_ => Json($"{{\"workflowid\":\"{workflowId}\",\"componentstate\":{componentState}}}"));

        Assert.Equal(expected, await client.HasUnpublishedDraftAsync(workflowId, CancellationToken.None));

        var exchange = Assert.Single(handler.Exchanges);

        Assert.Equal(HttpMethod.Get, exchange.Method);
        Assert.Equal($"{DataverseUrl}/api/data/v9.2/workflows({workflowId})/Microsoft.Dynamics.CRM.RetrieveUnpublished()?$select=componentstate", exchange.Url);
    }

    [Fact]
    public async Task HasUnpublishedDraftAsync_WhenTheWorkflowIsGone_ReportsNoDraft()
    {
        var (client, handler) = Create(_ => NotFound());

        Assert.False(await client.HasUnpublishedDraftAsync(Guid.NewGuid(), CancellationToken.None));
        Assert.Single(handler.Exchanges);
    }

    [Fact]
    public async Task HasUnpublishedDraftAsync_WhenTheServiceRefusesTheRead_ReportsTheFailure()
    {
        var (client, _) = Create(_ => Failure(HttpStatusCode.Forbidden, "{\"error\":\"denied\"}"));

        var failure = await Assert.ThrowsAsync<DataverseRequestException>(() => client.HasUnpublishedDraftAsync(Guid.NewGuid(), CancellationToken.None));

        Assert.Equal(HttpStatusCode.Forbidden, failure.StatusCode);
    }

    [Fact]
    public async Task ListWorkflowsAsync_ReadsEveryColumnTheListingShows()
    {
        var workflowId = Guid.NewGuid();
        var (client, _) = Create(_ => Json($"{{\"value\":[{{\"workflowid\":\"{workflowId}\",\"name\":\"Flow\",\"description\":\"d\",\"statecode\":1,\"statuscode\":2,\"modifiedon\":\"2026-01-02T03:04:05Z\"}}]}}"));

        var summary = Assert.Single(await client.ListWorkflowsAsync(null, null, CancellationToken.None));

        Assert.Equal(workflowId, summary.WorkflowId);
        Assert.Equal("Flow", summary.Name);
        Assert.Equal("d", summary.Description);
        Assert.Equal(1, summary.StateCode);
        Assert.Equal(2, summary.StatusCode);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), summary.ModifiedOn);
    }

    [Fact]
    public async Task ListWorkflowsAsync_FollowsPagingUntilTheServiceStops()
    {
        var first = $"{{\"value\":[{{\"workflowid\":\"{Guid.NewGuid()}\",\"name\":\"One\"}}],\"@odata.nextLink\":\"{DataverseUrl}/page2\"}}";
        var (client, handler) = Create(request => Json(request.RequestUri!.ToString().EndsWith("/page2", StringComparison.Ordinal)
            ? $"{{\"value\":[{{\"workflowid\":\"{Guid.NewGuid()}\",\"name\":\"Two\"}}]}}"
            : first));

        var summaries = await client.ListWorkflowsAsync(null, null, CancellationToken.None);

        Assert.Equal(["One", "Two"], summaries.Select(summary => summary.Name));
        Assert.Equal(2, handler.Exchanges.Count);
    }

    [Fact]
    public async Task ListWorkflowsAsync_StopsAtTheCapWithoutFollowingPaging()
    {
        var (client, handler) = Create(_ => Json($"{{\"value\":[{{\"workflowid\":\"{Guid.NewGuid()}\",\"name\":\"One\"}},{{\"workflowid\":\"{Guid.NewGuid()}\",\"name\":\"Two\"}}],\"@odata.nextLink\":\"{DataverseUrl}/page2\"}}"));

        var summaries = await client.ListWorkflowsAsync(null, 1, CancellationToken.None);

        Assert.Equal(["One"], summaries.Select(summary => summary.Name));
        Assert.Single(handler.Exchanges);
        Assert.Contains("$top=1", handler.Exchanges[0].Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InsertWorkflowAsync_WhenTheServiceRefuses_LeavesTheCallersStateUntouched()
    {
        var (client, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("refused", Encoding.UTF8, "application/json") });
        var workflow = new WorkflowMetadata { WorkflowId = Guid.NewGuid(), Name = "Standalone", ClientData = "{}", StateCode = 1, StatusCode = 2 };

        var response = await client.InsertWorkflowAsync(workflow, CancellationToken.None);

        Assert.NotEmpty(response.ErrorMessage);
        Assert.Equal(1, workflow.StateCode);
        Assert.Equal(2, workflow.StatusCode);
    }

    [Fact]
    public async Task InsertWorkflowAsync_WhenActivationFails_LeavesTheCallersStateUntouched()
    {
        var (client, _) = Create(request => request.Method == HttpMethod.Post
            ? Json("{}")
            : new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("ConnectionAuthorizationFailed", Encoding.UTF8, "application/json") });

        var workflow = new WorkflowMetadata { WorkflowId = Guid.NewGuid(), Name = "Standalone", ClientData = "{}", StateCode = 1, StatusCode = 2 };

        var response = await client.InsertWorkflowAsync(workflow, CancellationToken.None);

        Assert.NotEmpty(response.ErrorMessage);
        Assert.Equal(1, workflow.StateCode);
        Assert.Equal(2, workflow.StatusCode);
    }

    [Fact]
    public async Task WorkflowWrites_NeverCarryColumnsTheServiceOwns()
    {
        var (client, handler) = Create(_ => Json("{}"));

        await client.InsertWorkflowAsync(new WorkflowMetadata { WorkflowId = Guid.NewGuid(), Name = "Flow", ClientData = "{}", IsManaged = true }, CancellationToken.None);

        var post = Assert.Single(handler.Exchanges, exchange => exchange.Method == HttpMethod.Post);

        Assert.DoesNotContain("modifiedon", post.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("createdon", post.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ismanaged", post.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpdateWorkflowDefinitionAsync_WithoutADefinition_IsRefusedRatherThanClearingTheCloudCopy()
    {
        var (client, handler) = Create(_ => Json("{}"));

        await Assert.ThrowsAsync<ArgumentException>(() => client.UpdateWorkflowDefinitionAsync(new WorkflowMetadata { WorkflowId = Guid.NewGuid(), Name = "WF" }, CancellationToken.None));
        Assert.Empty(handler.Exchanges);
    }

    [Fact]
    public async Task UpdateWorkflowDefinitionAsync_WritesTheDescriptionSoAChangeToItIsNotLost()
    {
        var (client, handler) = Create(_ => Json("{}"));

        await client.UpdateWorkflowDefinitionAsync(new WorkflowMetadata { WorkflowId = Guid.NewGuid(), Name = "WF", ClientData = "{}", Description = "the new description" }, CancellationToken.None);

        Assert.Contains("the new description", Assert.Single(handler.Exchanges).Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateWorkflowDefinitionAsync_WritesOnlyTheDefinitionAndName()
    {
        var (client, handler) = Create(_ => Json("{}"));

        await client.UpdateWorkflowDefinitionAsync(new WorkflowMetadata { WorkflowId = Guid.NewGuid(), Name = "WF", ClientData = "{}", StateCode = 1, StatusCode = 2, Category = 5 }, CancellationToken.None);

        var body = Assert.Single(handler.Exchanges).Body;

        Assert.Contains("clientdata", body, StringComparison.Ordinal);
        Assert.Contains("name", body, StringComparison.Ordinal);
        Assert.DoesNotContain("statecode", body, StringComparison.Ordinal);
        Assert.DoesNotContain("category", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListWorkflowsAsync_WithACapOfZero_ReturnsNothingRatherThanTheWholeEnvironment()
    {
        var (client, handler) = Create(_ => Json("{\"value\":[]}"));

        Assert.Empty(await client.ListWorkflowsAsync(null, 0, CancellationToken.None));
        Assert.Empty(handler.Exchanges);
    }

    [Fact]
    public async Task ListWorkflowsAsync_WithANegativeCap_IsRefused()
    {
        var (client, _) = Create(_ => Json("{\"value\":[]}"));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.ListWorkflowsAsync(null, -1, CancellationToken.None));
    }

    [Fact]
    public async Task DeleteWorkflowAsync_WhenTheWorkflowIsADraft_ReadsTheStateThenDeletesWithoutStopping()
    {
        var workflowId = Guid.NewGuid();
        var (client, handler) = Create(request => request.Method == HttpMethod.Get
            ? Json($"{{\"workflowid\":\"{workflowId}\",\"statecode\":0}}")
            : Json("{}"));

        Assert.True(await client.DeleteWorkflowAsync(workflowId, CancellationToken.None));
        Assert.Contains(handler.Exchanges, exchange => exchange.Url.Contains("$select=workflowid,statecode", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Exchanges, exchange => exchange.Url.Contains("clientdata", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Exchanges, exchange => exchange.Body.Contains("statecode", StringComparison.Ordinal));
        Assert.Contains(handler.Exchanges, exchange => exchange.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task DeleteWorkflowAsync_WhenStoppingFailsForAnotherReason_DoesNotDelete()
    {
        var workflowId = Guid.NewGuid();
        var (client, handler) = Create(request => request.Method == HttpMethod.Get
            ? Json($"{{\"workflowid\":\"{workflowId}\",\"statecode\":1}}")
            : request.Method == HttpMethod.Delete
                ? Json("{}")
                : new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("the service is unavailable", Encoding.UTF8, "application/json") });

        await Assert.ThrowsAsync<DataverseRequestException>(() => client.DeleteWorkflowAsync(workflowId, CancellationToken.None));
        Assert.DoesNotContain(handler.Exchanges, exchange => exchange.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task DeleteWorkflowAsync_WhenStoppingIsRefusedOverAConnection_DoesNotDeleteAStillRunningWorkflow()
    {
        var workflowId = Guid.NewGuid();
        var (client, handler) = Create(request => request.Method == HttpMethod.Get
            ? Json($"{{\"workflowid\":\"{workflowId}\",\"statecode\":1}}")
            : request.Method == HttpMethod.Delete
                ? Json("{}")
                : new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("ConnectionAuthorizationFailed", Encoding.UTF8, "application/json") });

        var refusal = await Assert.ThrowsAsync<DataverseRequestException>(() => client.DeleteWorkflowAsync(workflowId, CancellationToken.None));

        Assert.Contains("ConnectionAuthorizationFailed", refusal.ResponseBody, StringComparison.Ordinal);
        Assert.DoesNotContain(handler.Exchanges, exchange => exchange.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task DeleteWorkflowAsync_WhenTheStateIsUnknown_StopsBeforeDeletingRatherThanAssumingADraft()
    {
        var workflowId = Guid.NewGuid();
        var (client, handler) = Create(request => request.Method == HttpMethod.Get
            ? Json($"{{\"workflowid\":\"{workflowId}\"}}")
            : Json("{}"));

        Assert.True(await client.DeleteWorkflowAsync(workflowId, CancellationToken.None));

        var draft = handler.Exchanges.FindIndex(exchange => exchange.Method.Method == "PATCH");

        Assert.True(draft >= 0);
        Assert.True(handler.Exchanges.FindIndex(exchange => exchange.Method == HttpMethod.Delete) > draft);
    }

    [Fact]
    public async Task DeleteWorkflowAsync_WhenTheWorkflowIsAbsent_DeletesNothing()
    {
        var (client, handler) = Create(_ => NotFound());

        Assert.False(await client.DeleteWorkflowAsync(Guid.NewGuid(), CancellationToken.None));
        Assert.DoesNotContain(handler.Exchanges, exchange => exchange.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task DeleteWorkflowAsync_WhenTheWorkflowIsRunning_StopsItBeforeDeleting()
    {
        var workflowId = Guid.NewGuid();
        var (client, handler) = Create(request => request.Method == HttpMethod.Get
            ? Json($"{{\"workflowid\":\"{workflowId}\",\"statecode\":1}}")
            : Json("{}"));

        Assert.True(await client.DeleteWorkflowAsync(workflowId, CancellationToken.None));

        var draft = handler.Exchanges.FindIndex(exchange => exchange.Method.Method == "PATCH");
        var delete = handler.Exchanges.FindIndex(exchange => exchange.Method == HttpMethod.Delete);

        Assert.True(draft >= 0);
        Assert.True(delete > draft);
        Assert.Contains("\"statecode\":0", handler.Exchanges[draft].Body.Replace(" ", string.Empty), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeleteWorkflowAsync_WhenTheServiceLosesItsTransaction_DeletesOnTheNextAttempt()
    {
        var workflowId = Guid.NewGuid();
        var deletes = 0;
        var (client, handler) = Create(request => request.Method == HttpMethod.Get
            ? Json($"{{\"workflowid\":\"{workflowId}\",\"statecode\":0}}")
            : ++deletes == 1
                ? Failure(HttpStatusCode.InternalServerError, NoActiveTransactionBody)
                : new HttpResponseMessage(HttpStatusCode.NoContent));

        Assert.True(await client.DeleteWorkflowAsync(workflowId, CancellationToken.None));
        Assert.Equal(2, handler.Exchanges.Count(exchange => exchange.Method == HttpMethod.Delete));
    }

    [Fact]
    public async Task DeleteWorkflowAsync_WhenAnEarlierAttemptAlreadyRemovedTheRow_ReportsItDeleted()
    {
        var workflowId = Guid.NewGuid();
        var deletes = 0;
        var (client, handler) = Create(request => request.Method == HttpMethod.Get
            ? Json($"{{\"workflowid\":\"{workflowId}\",\"statecode\":0}}")
            : ++deletes == 1
                ? Failure(HttpStatusCode.GatewayTimeout, string.Empty)
                : NotFound());

        Assert.True(await client.DeleteWorkflowAsync(workflowId, CancellationToken.None));
        Assert.Equal(2, handler.Exchanges.Count(exchange => exchange.Method == HttpMethod.Delete));
    }

    [Fact]
    public async Task DeleteWorkflowAsync_WhenTheServiceKeepsLosingItsTransaction_ReportsTheFailureAfterThreeAttempts()
    {
        var workflowId = Guid.NewGuid();
        var (client, handler) = Create(request => request.Method == HttpMethod.Get
            ? Json($"{{\"workflowid\":\"{workflowId}\",\"statecode\":0}}")
            : Failure(HttpStatusCode.InternalServerError, NoActiveTransactionBody));

        var failure = await Assert.ThrowsAsync<DataverseRequestException>(() => client.DeleteWorkflowAsync(workflowId, CancellationToken.None));

        Assert.Contains("0x80040251", failure.ResponseBody, StringComparison.Ordinal);
        Assert.Equal(3, handler.Exchanges.Count(exchange => exchange.Method == HttpMethod.Delete));
    }

    [Fact]
    public async Task UpdateWorkflowDefinitionAsync_WhenTheServiceRefuses_ReportsTheFailure()
    {
        var (client, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("ConnectionAuthorizationFailed", Encoding.UTF8, "application/json") });

        var response = await client.UpdateWorkflowDefinitionAsync(new WorkflowMetadata { WorkflowId = Guid.NewGuid(), Name = "WF", ClientData = "{}" }, CancellationToken.None);

        Assert.Contains("ConnectionAuthorizationFailed", response.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateWorkflowAsync_AsksForTheRowTheServiceStored_AndReturnsIt()
    {
        var workflowId = Guid.NewGuid();
        var preferences = new List<string>();
        var (client, handler) = Create(request =>
        {
            if (request.Method.Method == "PATCH")
            {
                preferences.AddRange(request.Headers.GetValues("Prefer"));
            }

            return Json(request.Method == HttpMethod.Get ? "{}" : $$"""{"workflowid":"{{workflowId}}","name":"Stored","clientdata":"{}","statecode":0,"statuscode":1}""");
        });

        var response = await client.UpdateWorkflowAsync(Guid.NewGuid(), new WorkflowMetadata { WorkflowId = workflowId, Name = "Local", ClientData = "{}", StateCode = 0, StatusCode = 1 }, CancellationToken.None);

        Assert.Equal(["return=representation"], preferences);
        Assert.Contains("$select=", Assert.Single(handler.Exchanges, exchange => exchange.Method.Method == "PATCH").Url, StringComparison.Ordinal);
        Assert.Equal("Stored", response.StoredWorkflow!.Name);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task InsertWorkflowAsync_ReturnsTheRowItsLastWriteStored(int stateCode)
    {
        var workflowId = Guid.NewGuid();
        var (client, _) = Create(request => Json(request.Method == HttpMethod.Post ? $$"""{"workflowid":"{{workflowId}}","statecode":0,"statuscode":1}""" : $$"""{"workflowid":"{{workflowId}}","statecode":1,"statuscode":2}"""));

        var response = await client.InsertWorkflowAsync(new WorkflowMetadata { WorkflowId = workflowId, Name = "Flow", ClientData = "{}", StateCode = stateCode, StatusCode = stateCode == 1 ? 2 : 1 }, CancellationToken.None);

        Assert.Equal(stateCode, response.StoredWorkflow!.StateCode);
    }

    [Fact]
    public async Task AWorkflowWrite_StopsAskingForAColumnTheEnvironmentLacks_AndStillReturnsTheRow()
    {
        var workflowId = Guid.NewGuid();
        var (client, handler) = Create(request => request.RequestUri!.Query.Contains("introducedversion", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":{\"message\":\"Could not find a property named 'introducedversion' on type 'Microsoft.Dynamics.CRM.workflow'.\"}}", Encoding.UTF8, "application/json") }
            : Json($$"""{"workflowid":"{{workflowId}}","name":"WF"}"""));

        var response = await client.UpdateWorkflowAsync(Guid.NewGuid(), new WorkflowMetadata { WorkflowId = workflowId, Name = "WF", ClientData = "{}" }, CancellationToken.None);
        var patches = handler.Exchanges.Where(exchange => exchange.Method.Method == "PATCH").ToList();

        Assert.Empty(response.ErrorMessage);
        Assert.Equal(workflowId, response.StoredWorkflow!.WorkflowId);
        Assert.Equal(2, patches.Count);
        Assert.Equal(patches[0].Body, patches[1].Body);
    }
}
