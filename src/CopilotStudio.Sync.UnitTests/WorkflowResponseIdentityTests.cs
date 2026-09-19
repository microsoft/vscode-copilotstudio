// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.Platform.Content.Abstractions;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;
using System.Net;
using System.Text;
using Xunit;
using static Microsoft.CopilotStudio.Sync.Dataverse.SyncDataverseClient;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class WorkflowResponseIdentityTests
{
    private static readonly Guid FirstWorkflowId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SecondWorkflowId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task UpdateWorkflowAsync_ReturnsTheWorkflowId()
    {
        var client = CreateClient(_ => Ok("{\"value\":[{\"workflowid\":\"11111111-1111-1111-1111-111111111111\"}]}"));

        var response = await client.UpdateWorkflowAsync(Guid.NewGuid(), CreateMetadata(FirstWorkflowId, "Shared", activated: true), CancellationToken.None);

        Assert.Equal(FirstWorkflowId, response.WorkflowId);
        Assert.Equal("Shared", response.WorkflowName);
        Assert.Empty(response.ErrorMessage);
    }

    [Fact]
    public async Task InsertWorkflowAsync_ReturnsTheWorkflowId()
    {
        var client = CreateClient(_ => Ok("{}"));

        var response = await client.InsertWorkflowAsync(Guid.NewGuid(), CreateMetadata(SecondWorkflowId, "Shared", activated: false), CancellationToken.None);

        Assert.Equal(SecondWorkflowId, response.WorkflowId);
        Assert.Equal("Shared", response.WorkflowName);
    }

    [Fact]
    public async Task UpdateWorkflowAsync_WhenTheRequestFails_StillReturnsTheWorkflowId()
    {
        var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("{\"error\":{\"message\":\"boom\"}}", Encoding.UTF8, "application/json"),
        });

        var response = await client.UpdateWorkflowAsync(Guid.NewGuid(), CreateMetadata(FirstWorkflowId, "Shared", activated: true), CancellationToken.None);

        Assert.Equal(FirstWorkflowId, response.WorkflowId);
        Assert.NotEmpty(response.ErrorMessage);
    }

    [Fact]
    public async Task WorkflowsSharingADisplayName_RemainDistinguishableById()
    {
        var client = CreateClient(_ => Ok("{\"value\":[{\"workflowid\":\"11111111-1111-1111-1111-111111111111\"}]}"));

        var first = await client.UpdateWorkflowAsync(Guid.NewGuid(), CreateMetadata(FirstWorkflowId, "Shared", activated: true), CancellationToken.None);
        var second = await client.UpdateWorkflowAsync(Guid.NewGuid(), CreateMetadata(SecondWorkflowId, "Shared", activated: false), CancellationToken.None);

        Assert.Equal(first.WorkflowName, second.WorkflowName);
        Assert.NotEqual(first.WorkflowId, second.WorkflowId);

        var byId = new[] { first, second }.ToDictionary(response => response.WorkflowId, response => !response.IsDisabled);

        Assert.Equal(2, byId.Count);
        Assert.True(byId[FirstWorkflowId]);
        Assert.False(byId[SecondWorkflowId]);
    }

    [Fact]
    public async Task WorkflowId_SurvivesTheUpsertProjection()
    {
        var client = CreateClient(_ => Ok("{\"value\":[{\"workflowid\":\"11111111-1111-1111-1111-111111111111\"}]}"));

        var response = await client.UpdateWorkflowAsync(Guid.NewGuid(), CreateMetadata(FirstWorkflowId, name: null, activated: true), CancellationToken.None);

        Assert.Equal(FirstWorkflowId, response.WorkflowId);
        Assert.Equal(FirstWorkflowId.ToString(), response.WorkflowName);
    }

    private static WorkflowMetadata CreateMetadata(Guid workflowId, string? name, bool activated) => new()
    {
        WorkflowId = workflowId,
        Name = name,
        StateCode = activated ? 1 : 0,
        StatusCode = activated ? 2 : 1,
    };

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static SyncDataverseClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        var accessor = new Mock<IDataverseHttpClientAccessor>();
        accessor.Setup(a => a.CreateClient()).Returns(new HttpClient(new StubHandler(handler)));
        var client = new SyncDataverseClient(accessor.Object);
        client.SetDataverseUrl("https://test.crm.dynamics.com");
        return client;
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_handler(request));
    }
}
