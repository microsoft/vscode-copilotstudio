// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.Platform.Content.Abstractions;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;
using System.Net;
using System.Text;
using Xunit;
using static Microsoft.CopilotStudio.Sync.Dataverse.SyncDataverseClient;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class StandaloneWorkflowDataverseTests
{
    private const string DataverseUrl = "https://test.crm.dynamics.com";

    private sealed record Exchange(HttpMethod Method, string Url, string Body);

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
                request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync()));

            return _respond(request);
        }
    }

    private static HttpResponseMessage Json(string payload) =>
        new(HttpStatusCode.OK) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage NotFound() =>
        new(HttpStatusCode.NotFound) { Content = new StringContent("{\"error\":\"not found\"}", Encoding.UTF8, "application/json") };

    private static (SyncDataverseClient Client, RecordingHandler Handler) Create(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new RecordingHandler(respond);
        var accessor = new Mock<IDataverseHttpClientAccessor>();
        accessor.Setup(a => a.CreateClient()).Returns(new HttpClient(handler));

        var client = new SyncDataverseClient(accessor.Object);
        client.SetDataverseUrl(DataverseUrl);

        return (client, handler);
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
        Assert.Equal(1, workflow.StateCode);
        Assert.Equal(2, workflow.StatusCode);
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

        await client.InsertWorkflowAsync(new WorkflowMetadata { WorkflowId = Guid.NewGuid(), Name = "Flow", ClientData = "{}" }, CancellationToken.None);

        var post = Assert.Single(handler.Exchanges, exchange => exchange.Method == HttpMethod.Post);

        Assert.DoesNotContain("modifiedon", post.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("createdon", post.Body, StringComparison.OrdinalIgnoreCase);
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
    public async Task UpdateWorkflowDefinitionAsync_WhenTheServiceRefuses_ReportsTheFailure()
    {
        var (client, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("ConnectionAuthorizationFailed", Encoding.UTF8, "application/json") });

        var response = await client.UpdateWorkflowDefinitionAsync(new WorkflowMetadata { WorkflowId = Guid.NewGuid(), Name = "WF", ClientData = "{}" }, CancellationToken.None);

        Assert.Contains("ConnectionAuthorizationFailed", response.ErrorMessage, StringComparison.Ordinal);
    }
}
