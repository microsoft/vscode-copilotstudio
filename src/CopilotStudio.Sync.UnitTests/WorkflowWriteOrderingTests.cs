// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.Platform.Content.Abstractions;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;
using System.Net;
using System.Text;
using Xunit;
using static Microsoft.CopilotStudio.Sync.Dataverse.SyncDataverseClient;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class WorkflowWriteOrderingTests
{
    private const string DataverseUrl = "https://test.crm.dynamics.com";

    [Fact]
    public async Task UpdateWorkflowAsync_WritesClientDataBeforeActivationState()
    {
        var body = await CapturePatchBodyAsync(new WorkflowMetadata
        {
            WorkflowId = Guid.NewGuid(),
            Name = "AgentFlow",
            ClientData = "{}",
            StateCode = 1,
            StatusCode = 2
        });

        AssertClientDataPrecedesActivationState(body);
    }

    [Fact]
    public async Task InsertWorkflowAsync_WritesClientDataBeforeActivationState()
    {
        string? postBody = null;
        var workflow = new WorkflowMetadata
        {
            WorkflowId = Guid.NewGuid(),
            Name = "AgentFlow",
            ClientData = "{}",
            StateCode = 0,
            StatusCode = 1
        };

        var handler = new StubHandler(async (request, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                postBody = request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync();
                return new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });

        await CreateClient(handler).InsertWorkflowAsync(Guid.NewGuid(), workflow, CancellationToken.None);

        Assert.NotNull(postBody);
        Assert.Contains("\"clientdata\"", postBody!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateWorkflowAsync_WritesEveryColumnTheLegacyBodyCarried()
    {
        var body = await CapturePatchBodyAsync(new WorkflowMetadata
        {
            WorkflowId = Guid.NewGuid(),
            Name = "AgentFlow",
            Type = 1,
            Description = "description",
            Subprocess = false,
            Category = 5,
            Mode = 0,
            Scope = 4,
            OnDemand = true,
            TriggerOnCreate = false,
            TriggerOnDelete = false,
            AsyncAutodelete = false,
            SyncWorkflowLogOnFailure = false,
            RunAs = 1,
            IsTransacted = true,
            IntroducedVersion = "1.0",
            IsCustomizable = new ManagedProperty { Value = true },
            BusinessProcessType = 0,
            IsCustomProcessingStepAllowedForOtherPublishers = new ManagedProperty { Value = true },
            ModernFlowType = 1,
            PrimaryEntity = "none",
            ClientData = "{}",
            StateCode = 1,
            StatusCode = 2
        });

        var expected = new[]
        {
            "name", "type", "description", "subprocess", "category", "mode", "scope", "ondemand",
            "triggeroncreate", "triggerondelete", "asyncautodelete", "syncworkflowlogonfailure",
            "runas", "istransacted", "introducedversion", "iscustomizable", "businessprocesstype",
            "iscustomprocessingstepallowedforotherpublishers", "modernflowtype", "primaryentity",
            "clientdata", "statecode", "statuscode"
        };

        var actual = expected.Select(column => (Column: column, Index: body.IndexOf($"\"{column}\"", StringComparison.Ordinal))).ToArray();

        Assert.All(actual, entry => Assert.True(entry.Index >= 0, $"missing column '{entry.Column}'"));
        Assert.Equal(expected, actual.OrderBy(entry => entry.Index).Select(entry => entry.Column));
        Assert.DoesNotContain("\"jsonfilename\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"workflowid\"", body, StringComparison.Ordinal);
    }

    private static void AssertClientDataPrecedesActivationState(string body)
    {
        var clientData = body.IndexOf("\"clientdata\"", StringComparison.Ordinal);
        var stateCode = body.IndexOf("\"statecode\"", StringComparison.Ordinal);
        var statusCode = body.IndexOf("\"statuscode\"", StringComparison.Ordinal);

        Assert.True(clientData >= 0, "clientdata missing from workflow write");
        Assert.True(stateCode >= 0, "statecode missing from workflow write");
        Assert.True(statusCode >= 0, "statuscode missing from workflow write");
        Assert.True(clientData < stateCode, "statecode must not be applied before clientdata");
        Assert.True(clientData < statusCode, "statuscode must not be applied before clientdata");
    }

    private static async Task<string> CapturePatchBodyAsync(WorkflowMetadata workflow)
    {
        string? patchBody = null;

        var handler = new StubHandler(async (request, _) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            patchBody = request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });

        await CreateClient(handler).UpdateWorkflowAsync(Guid.NewGuid(), workflow, CancellationToken.None);

        Assert.NotNull(patchBody);
        return patchBody!;
    }

    private static SyncDataverseClient CreateClient(HttpMessageHandler handler)
    {
        var accessor = new Mock<IDataverseHttpClientAccessor>();
        accessor.Setup(a => a.CreateClient()).Returns(new HttpClient(handler));
        var client = new SyncDataverseClient(accessor.Object);
        client.SetDataverseUrl(DataverseUrl);
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
