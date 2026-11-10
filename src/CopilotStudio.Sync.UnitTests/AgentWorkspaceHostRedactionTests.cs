// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.CopilotStudio.Sync.Hosting;
using Moq;
using Xunit;
using static Microsoft.CopilotStudio.Sync.UnitTests.AgentWorkspaceHostPushTests;
using static Microsoft.CopilotStudio.Sync.UnitTests.DataverseFailureTextTests;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class AgentWorkspaceHostRedactionTests
{
    private readonly HostedCloud _cloud = new();

    private readonly RecordingAuthProvider _authentication = new();

    public static TheoryData<string, string> Failures => new()
    {
        { "publish", AgentSyncDiagnosticCodes.PublishFailed },
        { "publish-progress", AgentSyncDiagnosticCodes.PublishFailed },
        { "connector-refusal", AgentSyncDiagnosticCodes.ConnectorRefused },
        { "workflow-refusal", AgentSyncDiagnosticCodes.WorkflowRefused },
        { "missing-after-refusals", AgentWorkspaceHostErrorCodes.PushFailed },
        { "component-save", AgentWorkspaceHostErrorCodes.PushFailed },
        { "bot-create", AgentWorkspaceHostErrorCodes.PushFailed },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task PushAsync_WhenDataverseFailsWithAResponseBody_DescribesTheFailureWithoutTheBodyInEveryTextTheCallerSees(string failure, string code)
    {
        Arrange(failure);
        var host = _cloud.CreateHost();
        var snapshot = WithConnector(WithWorkflow(Scaffolds.Agent("Agent", "Contoso Helpdesk", SchemaName, AgentAuthoringModes.Classic), "Weather", Guid.NewGuid(), "{}"), Guid.NewGuid());

        var texts = await TextsTheCallerSeesAsync(() => host.PushAsync(snapshot, HostedCloud.Target(SchemaName), HostedCloud.Options(), _authentication, CancellationToken.None));

        Assert.Contains(texts, text => text.Code == code && text.Message.Contains(ReportedDescription, StringComparison.Ordinal));
        Assert.All(texts, text => Assert.DoesNotContain(Marker, text.Message, StringComparison.Ordinal));
    }

    private static async Task<IReadOnlyList<(string Code, string Message)>> TextsTheCallerSeesAsync(Func<Task<AgentPushResult>> push)
    {
        try
        {
            return (await push()).Diagnostics.Select(diagnostic => (diagnostic.Code, diagnostic.Message)).ToArray();
        }
        catch (AgentWorkspaceHostException failure)
        {
            return failure.Diagnostics.Select(diagnostic => (diagnostic.Code, diagnostic.Message)).Prepend((failure.Code, failure.Message)).ToArray();
        }
    }

    private void Arrange(string failure)
    {
        switch (failure)
        {
            case "publish":
                _cloud.PublishThrows = Failure();
                break;
            case "publish-progress":
                _cloud.PublishProgressThrows = Failure();
                break;
            case "connector-refusal":
                _cloud.Cloud.Client.Setup(client => client.UpsertConnectorAsync(It.IsAny<CustomConnectorMetadata>(), It.IsAny<CancellationToken>())).ThrowsAsync(Failure());
                break;
            case "workflow-refusal":
                _cloud.RefusesWorkflow = _ => Failure().Message;
                break;
            case "missing-after-refusals":
                _cloud.RefusesWorkflow = _ => Failure().Message;
                _cloud.Island.SaveFailures.Enqueue(Failure());
                break;
            case "component-save":
                _cloud.Island.SaveFailures.Enqueue(Failure());
                break;
            default:
                _cloud.CreateThrows = Failure();
                break;
        }
    }
}
