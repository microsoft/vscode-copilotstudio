// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Net;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Microsoft.CopilotStudio.Sync.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;
using static Microsoft.CopilotStudio.Sync.UnitTests.AgentWorkspaceHostPushTests;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public sealed class AgentWorkspaceHostChangeProbeTests
{
    private static readonly AgentChangeProbe Probe = new(new(1, new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero)), new(0, null), new(1, new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero)), new(0, null), new(0, null), new(0, null), new(1, new DateTimeOffset(2026, 11, 3, 11, 0, 0, TimeSpan.Zero)), new(1, new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero)), new(1, new DateTimeOffset(2026, 11, 5, 13, 0, 0, TimeSpan.Zero)), new(1, new DateTimeOffset(2026, 11, 6, 14, 0, 0, TimeSpan.Zero)));

    private readonly HostedCloud _cloud = new();

    private readonly RecordingFileAccessorFactory _files = new();

    private readonly RecordingAuthProvider _authentication = new();

    [Fact]
    public async Task ProbeAsync_ReadsTheProbeOfTheBotTheTargetNames()
    {
        var botId = _cloud.SeedAgent(SchemaName, "Contoso Helpdesk");
        _cloud.ChangeProbe.Setup(changeProbe => changeProbe.ProbeAgentAsync(botId, SchemaName, It.IsAny<CancellationToken>())).ReturnsAsync(Probe);

        Assert.Same(Probe, await _cloud.CreateHost(_files).ProbeAsync(HostedCloud.Target(SchemaName, botId.ToString()), _authentication, CancellationToken.None));
    }

    [Fact]
    public async Task ProbeAsync_OfABotTheEnvironmentDoesNotHold_ThrowsBotNotFoundAfterOneReadOfTheOrganization()
    {
        var handlers = new StubHandlerFactory(_ => AgentWorkspaceHostIsolationTests.Json("{\"value\":[]}"));
        using var provider = AgentWorkspaceHostIsolationTests.Registered(handlers);

        var failure = await Assert.ThrowsAsync<AgentWorkspaceHostException>(() => provider.GetRequiredService<IAgentWorkspaceHost>().ProbeAsync(HostedCloud.Target(SchemaName), _authentication, CancellationToken.None));

        Assert.Equal((AgentWorkspaceHostErrorCodes.BotNotFound, $"The bot '{SchemaName}' does not exist in {HostedCloud.EnvironmentId}."), (failure.Code, failure.Message));
        Assert.StartsWith(new Uri(HostedCloud.Organization, "api/data/v9.2/bots?").AbsoluteUri, Assert.Single(handlers.Requests).RequestUri!.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProbeAsync_WhenTheEnvironmentFailsTheRead_ThrowsLookupFailed()
    {
        _cloud.ChangeProbe.Setup(changeProbe => changeProbe.ProbeAgentAsync(It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new DataverseRequestException(HttpStatusCode.Forbidden, "The caller may not read bots."));

        Assert.Equal(AgentWorkspaceHostErrorCodes.LookupFailed, (await Assert.ThrowsAsync<AgentWorkspaceHostException>(() => _cloud.CreateHost(_files).ProbeAsync(HostedCloud.Target(SchemaName), _authentication, CancellationToken.None))).Code);
    }

    [Fact]
    public async Task PushAsync_ReturnsTheProbeOfThePushedBotReadOnceItsPublishFinished()
    {
        var probed = ((Guid?)null, -1);
        _cloud.ChangeProbe.Setup(changeProbe => changeProbe.ProbeAgentAsync(It.IsAny<Guid?>(), SchemaName, It.IsAny<CancellationToken>())).Returns<Guid?, string, CancellationToken>((botId, _, _) =>
        {
            probed = (botId, _cloud.Published.Count);

            return Task.FromResult<AgentChangeProbe?>(Probe);
        });

        var result = await _cloud.CreateHost(_files).PushAsync(AgentWorkspaceHostPushStageTests.Scaffold(), HostedCloud.Target(SchemaName), HostedCloud.Options() with { ReadChangeProbe = true }, _authentication, CancellationToken.None);

        Assert.Same(Probe, result.ChangeProbe);
        Assert.Equal(((Guid?)Guid.Parse(result.BotId), 1), probed);
    }

    [Fact]
    public async Task PushAsync_WhenTheProbeCannotBeRead_SucceedsWithoutAProbe()
    {
        _cloud.ChangeProbe.Setup(changeProbe => changeProbe.ProbeAgentAsync(It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new DataverseRequestException(HttpStatusCode.ServiceUnavailable, "The service is busy."));

        var result = await _cloud.CreateHost(_files).PushAsync(AgentWorkspaceHostPushStageTests.Scaffold(), HostedCloud.Target(SchemaName), HostedCloud.Options() with { ReadChangeProbe = true }, _authentication, CancellationToken.None);

        Assert.True(result.Published);
        Assert.Null(result.ChangeProbe);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public async Task CloneAsync_ReturnsTheProbeOfTheBotReadBeforeTheCloneReadsAnything()
    {
        var botId = _cloud.SeedAgent(SchemaName, "Contoso Helpdesk", PushComponentCollectionExclusionTests.Topic($"{SchemaName}.topic.Greeting", "Greeting"));
        var probed = ((Guid?)null, -1);
        _cloud.ChangeProbe.Setup(changeProbe => changeProbe.ProbeAgentAsync(It.IsAny<Guid?>(), SchemaName, It.IsAny<CancellationToken>())).Returns<Guid?, string, CancellationToken>((probedBotId, _, _) =>
        {
            probed = (probedBotId, _cloud.DataverseCalls().Count + _cloud.Island.Service.Invocations.Count);

            return Task.FromResult<AgentChangeProbe?>(Probe);
        });

        var result = await _cloud.CreateHost(_files).CloneAsync(CloneRequest(botId) with { ReadChangeProbe = true }, _authentication, CancellationToken.None);

        Assert.Same(Probe, result.ChangeProbe);
        Assert.Equal(((Guid?)botId, 0), probed);
        Assert.NotEmpty(_cloud.DataverseCalls());
    }

    [Fact]
    public async Task CloneAsync_WhenTheProbeCannotBeRead_ClonesWithoutAProbe()
    {
        var botId = _cloud.SeedAgent(SchemaName, "Contoso Helpdesk");
        _cloud.ChangeProbe.Setup(changeProbe => changeProbe.ProbeAgentAsync(It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new DataverseRequestException(HttpStatusCode.ServiceUnavailable, "The service is busy."));

        var result = await _cloud.CreateHost(_files).CloneAsync(CloneRequest(botId) with { ReadChangeProbe = true }, _authentication, CancellationToken.None);

        Assert.Equal(botId.ToString(), result.BotId);
        Assert.Null(result.ChangeProbe);
    }

    [Fact]
    public async Task CloneAsync_CancelledWhileReadingTheProbe_IsCancelled()
    {
        var botId = _cloud.SeedAgent(SchemaName, "Contoso Helpdesk");
        using var cancellation = new CancellationTokenSource();
        _cloud.ChangeProbe.Setup(changeProbe => changeProbe.ProbeAgentAsync(It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns<Guid?, string, CancellationToken>((_, _, cancellationToken) =>
        {
            cancellation.Cancel();

            return Task.FromCanceled<AgentChangeProbe?>(cancellationToken);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _cloud.CreateHost(_files).CloneAsync(CloneRequest(botId) with { ReadChangeProbe = true }, _authentication, cancellation.Token));
        Assert.Empty(_cloud.DataverseCalls());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PushAsync_SendsTheProbeRequestOnlyWhenAskedTo(bool readChangeProbe)
    {
        var handlers = new StubHandlerFactory(_ => AgentWorkspaceHostIsolationTests.Json("{\"value\":[]}"));

        var result = await _cloud.CreateHost(_files, handlers, ProbeThroughDataverse).PushAsync(AgentWorkspaceHostPushStageTests.Scaffold(), HostedCloud.Target(SchemaName), HostedCloud.Options() with { ReadChangeProbe = readChangeProbe }, _authentication, CancellationToken.None);

        Assert.True(result.Published);
        Assert.Equal(readChangeProbe ? new[] { "/api/data/v9.2/bots" } : Array.Empty<string>(), handlers.Requests.Select(request => request.RequestUri!.AbsolutePath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CloneAsync_SendsTheProbeRequestOnlyWhenAskedTo(bool readChangeProbe)
    {
        var botId = _cloud.SeedAgent(SchemaName, "Contoso Helpdesk");
        var handlers = new StubHandlerFactory(_ => AgentWorkspaceHostIsolationTests.Json("{\"value\":[]}"));

        var result = await _cloud.CreateHost(_files, handlers, ProbeThroughDataverse).CloneAsync(CloneRequest(botId) with { ReadChangeProbe = readChangeProbe }, _authentication, CancellationToken.None);

        Assert.Equal(botId.ToString(), result.BotId);
        Assert.Equal(readChangeProbe ? new[] { "/api/data/v9.2/bots" } : Array.Empty<string>(), handlers.Requests.Select(request => request.RequestUri!.AbsolutePath));
    }

    [Fact]
    public async Task PushAsync_ReturnsTheProbeOfEveryTableTheCaptureReadsAsReadThroughDataverse()
    {
        Assert.Equal(Probe, (await _cloud.CreateHost(_files, new StubHandlerFactory(AnswerEveryProbedTable), ProbeThroughDataverse).PushAsync(AgentWorkspaceHostPushStageTests.Scaffold(), HostedCloud.Target(SchemaName), HostedCloud.Options() with { ReadChangeProbe = true }, _authentication, CancellationToken.None)).ChangeProbe);
    }

    [Fact]
    public async Task CloneAsync_ReturnsTheProbeOfEveryTableTheCaptureReadsAsReadThroughDataverse()
    {
        Assert.Equal(Probe, (await _cloud.CreateHost(_files, new StubHandlerFactory(AnswerEveryProbedTable), ProbeThroughDataverse).CloneAsync(CloneRequest(_cloud.SeedAgent(SchemaName, "Contoso Helpdesk")) with { ReadChangeProbe = true }, _authentication, CancellationToken.None)).ChangeProbe);
    }

    private static void ProbeThroughDataverse(IServiceCollection services) => services.AddSingleton<IAgentChangeProbeDataverseClient>(provider => provider.GetRequiredService<SyncDataverseClient>());

    private static HttpResponseMessage AnswerEveryProbedTable(HttpRequestMessage request) => AgentWorkspaceHostIsolationTests.Json(request.RequestUri!.AbsolutePath switch
    {
        "/api/data/v9.2/bots" => "{\"value\":[{\"botid\":\"6a0f6f3e-2b1c-4d8e-9a7b-1c2d3e4f5a6b\",\"modifiedon\":\"2026-11-01T09:00:00Z\"}]}",
        "/api/data/v9.2/botcomponents" => "{\"value\":[{\"botcomponentid\":\"0b7e2d5c-8f1a-4c3b-9e6d-2a4b6c8d0e1f\",\"modifiedon\":\"2026-11-02T10:00:00Z\",\"botcomponent_connectionreference\":[{\"connectionreferenceid\":\"1d3f5b7a-9c2e-4a6b-8d0f-2e4a6c8b0d1f\",\"connectorid\":\"/providers/Microsoft.PowerApps/apis/shared_cr123-5fcontoso\",\"modifiedon\":\"2026-11-03T11:00:00Z\"}],\"botcomponent_environmentvariabledefinition\":[{\"environmentvariabledefinitionid\":\"3f5b7d9a-1c3e-4d6f-8a0b-6c8e0a2c4e6a\",\"modifiedon\":\"2026-11-05T13:00:00Z\"}]}]}",
        "/api/data/v9.2/connectors" => "{\"value\":[{\"connectorid\":\"2e4a6c8b-0d1f-4b7a-9c3e-5f7b9d1a3c5e\",\"modifiedon\":\"2026-11-04T12:00:00Z\"}]}",
        "/api/data/v9.2/environmentvariablevalues" => "{\"value\":[{\"environmentvariablevalueid\":\"4a6c8e0b-2d4f-4e8a-9b1c-7d9f1b3d5f7b\",\"modifiedon\":\"2026-11-06T14:00:00Z\"}]}",
        _ => "{\"value\":[]}",
    });

    private static AgentCloneRequest CloneRequest(Guid botId) => new(HostedCloud.Target(SchemaName, botId.ToString()), "Agent", new Dictionary<string, string>(), Array.Empty<string>());

}
