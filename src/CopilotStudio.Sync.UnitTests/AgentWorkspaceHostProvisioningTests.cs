// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Net;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Microsoft.CopilotStudio.Sync.Hosting;
using Moq;
using Xunit;
using static Microsoft.CopilotStudio.Sync.UnitTests.AgentPublishServiceTests;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class AgentWorkspaceHostProvisioningTests
{
    private const string Provisioning = "Provisioning";

    private const string SchemaName = ClassicAgentFixtures.AgentD1Schema;

    private const string CliSchemaName = AgentWorkspaceHostPushTests.SchemaName;

    private readonly HostedCloud _cloud = new();

    private readonly RecordingAuthProvider _authentication = new();

    public static TheoryData<bool> FileModes => new() { true, false };

    public static TheoryData<bool, bool> FileModesAndBotExistence => new() { { true, false }, { true, true }, { false, false }, { false, true } };

    public static TheoryData<string?> StatusesOtherThanProvisioning => new() { null, "Provisioned", "Failed", string.Empty };

    public static TheoryData<Exception> UnreadableStatuses => new()
    {
        new FormatException("secret detail"),
        new DataverseRequestException(HttpStatusCode.Forbidden, "secret detail"),
        new DataverseRequestException(HttpStatusCode.ServiceUnavailable, "{}"),
        new HttpRequestException("The connection was reset."),
    };

    [Theory]
    [MemberData(nameof(FileModes))]
    public async Task PushAsync_ToANewBotCopilotStudioIsStillProvisioning_WaitsOnTheProvisioningBackoffBeforeWritingAnythingIntoIt(bool inMemory)
    {
        _cloud.ReportProvisioningStatuses(Provisioning, Provisioning, Provisioning, "Provisioned");

        var result = await PushAsync(inMemory);

        var reads = _cloud.ProvisioningReads.ToArray();
        Assert.True(result.Published);
        Assert.Equal(Enumerable.Repeat(Guid.Parse(result.BotId), 5), reads.Select(read => read.AgentId));
        Assert.All(reads.Take(4), AssertNothingWasWrittenBefore);
        Assert.NotEqual(0, reads[4].SavesSoFar);
        Assert.Contains(nameof(ISyncDataverseClient.UpsertConnectorAsync), _cloud.ContentWritesAmong(int.MaxValue));
        Assert.Equal(Seconds(2, 3, 4, 1), _cloud.Clock.Delays);
    }

    [Theory]
    [MemberData(nameof(FileModes))]
    public async Task PushAsync_WhenTheNewBotIsStillProvisioningAtTheTimeout_FailsAtTheAgentStageWithoutWritingAnythingAndTheNextPushReusesTheBot(bool inMemory)
    {
        _cloud.ProvisioningStatus = Provisioning;

        var failure = await Assert.ThrowsAsync<AgentWorkspaceHostException>(() => PushAsync(inMemory, HostedCloud.Options() with { ProvisioningTimeout = TimeSpan.FromSeconds(10) }));

        var botId = _cloud.AgentIdOf(SchemaName);
        var message = $"Copilot Studio was still provisioning the classic bot '{botId}' after 00:00:10, so nothing was pushed into it. The bot is kept, and a later deploy pushes into the same bot once Copilot Studio finishes provisioning it.";
        Assert.Equal((AgentWorkspaceHostErrorCodes.PushFailed, message), (failure.Code, failure.Message));
        Assert.Equal(new[] { (AgentSyncDiagnosticCodes.PushStageFailed, AgentPushStages.Agent), (AgentSyncDiagnosticCodes.AgentNotProvisioned, botId.ToString()) }, failure.Diagnostics.Select(diagnostic => (diagnostic.Code, diagnostic.Subject!)));
        Assert.Equal(message, failure.Diagnostics[1].Message);
        Assert.Equal(Seconds(2, 3, 4, 1), _cloud.Clock.Delays);
        Assert.Empty(_cloud.Island.Saves);
        Assert.Empty(_cloud.ContentWritesAmong(int.MaxValue));

        _cloud.ProvisioningStatus = "Provisioned";
        var redeployed = await PushAsync(inMemory);

        Assert.Equal((botId.ToString(), true), (redeployed.BotId, redeployed.Published));
        Assert.Equal(new[] { SchemaName }, _cloud.CreatedAgents);
    }

    [Fact]
    public async Task PushAsync_ToAnExistingProvisionedBot_ReadsItsStatusOnceBeforeWritingAndDoesNotWait()
    {
        var botId = _cloud.SeedAgent(SchemaName, "Agent D1");
        _cloud.ProvisioningStatus = "Provisioned";

        var result = await PushAsync(inMemory: true);

        var reads = _cloud.ProvisioningReads.ToArray();
        Assert.Equal(botId.ToString(), result.BotId);
        Assert.Equal(2, reads.Length);
        AssertNothingWasWrittenBefore(reads[0]);
        Assert.NotEqual(0, reads[1].SavesSoFar);
        Assert.Equal(Seconds(1), _cloud.Clock.Delays);
    }

    [Theory]
    [MemberData(nameof(StatusesOtherThanProvisioning))]
    public async Task PushAsync_WhenTheBotReportsNoProvisioningStatusOrAnotherOne_DoesNotWait(string? status)
    {
        _cloud.ProvisioningStatus = status;

        Assert.True((await PushAsync(inMemory: true)).Published);
        Assert.Equal(2, _cloud.ProvisioningReads.Count);
        Assert.Equal(Seconds(1), _cloud.Clock.Delays);
    }

    [Theory]
    [MemberData(nameof(UnreadableStatuses))]
    public async Task PushAsync_WhenTheProvisioningStatusCannotBeRead_PushesAndPublishesWithoutWaiting(Exception failure)
    {
        _cloud.ProvisioningThrows = failure;

        Assert.True((await PushAsync(inMemory: true)).Published);
        Assert.Equal(2, _cloud.ProvisioningReads.Count);
        Assert.Equal(Seconds(1), _cloud.Clock.Delays);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task PushAsync_WithAProvisioningTimeoutThatIsNotPositive_IsRefusedBeforeAnyCloudCall(int timeoutSeconds)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => PushAsync(inMemory: true, HostedCloud.Options() with { ProvisioningTimeout = TimeSpan.FromSeconds(timeoutSeconds) }));

        Assert.Empty(_cloud.Cloud.Client.Invocations);
    }

    [Fact]
    public async Task PushAsync_WhenCanceledWhileTheBotIsProvisioning_StopsBeforeWritingAnythingAndReleasesTheSession()
    {
        using var cancellation = new CancellationTokenSource();
        var files = new RecordingFileAccessorFactory();
        _cloud.ProvisioningStatus = Provisioning;
        _cloud.ProvisioningRead = () =>
        {
            if (_cloud.ProvisioningReads.Count == 2)
            {
                cancellation.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _cloud.CreateHost(files).PushAsync(ClassicAgentFixtures.AgentD1("Agent D1"), HostedCloud.Target(SchemaName), HostedCloud.Options(), _authentication, cancellation.Token));

        Assert.Equal(2, _cloud.ProvisioningReads.Count);
        Assert.Empty(_cloud.Island.Saves);
        Assert.Empty(_cloud.ContentWritesAmong(int.MaxValue));
        Assert.Equal(0, files.LiveCount);
    }

    [Fact]
    public async Task PushAsync_WhenTheBotIsProvisioningAgainBeforeThePublish_WaitsAndThenPublishes()
    {
        _cloud.ReportProvisioningStatuses("Provisioned", Provisioning, "Provisioned");

        var result = await PushAsync(inMemory: true);

        var reads = _cloud.ProvisioningReads.ToArray();
        Assert.True(result.Published);
        Assert.Equal(3, reads.Length);
        Assert.All(reads.Skip(1), read => Assert.NotEqual(0, read.SavesSoFar));
        Assert.Equal(Seconds(2, 1), _cloud.Clock.Delays);
        Assert.Single(_cloud.Published);
    }

    [Fact]
    public async Task PushAsync_WhenTheBotIsStillProvisioningWhenThePublishTimeoutPasses_ReportsItUnpublishedWithoutAskingForAPublish()
    {
        _cloud.ReportProvisioningStatuses("Provisioned");
        _cloud.ProvisioningStatus = Provisioning;

        var result = await PushAsync(inMemory: true, HostedCloud.Options() with { PublishTimeout = TimeSpan.FromSeconds(10) });

        Assert.False(result.Published);
        Assert.Contains(new AgentSyncDiagnostic(AgentSyncDiagnosticCodes.PublishTimedOut, "The bot was not published: Copilot Studio was still provisioning the agent after 00:00:10, so no publish was asked for. Deploy again once Copilot Studio finishes provisioning it."), result.Diagnostics);
        Assert.Equal(Seconds(2, 3, 4, 1), _cloud.Clock.Delays);
        Assert.NotEmpty(_cloud.Island.Saves);
        _cloud.Publisher.Verify(publisher => publisher.PublishAgentAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    [Theory]
    [MemberData(nameof(FileModesAndBotExistence))]
    public async Task PushAsync_OfACliAgentToABotThatStaysProvisioningUntilItsFirstPublish_WritesAndPublishesItWithoutWaiting(bool inMemory, bool botExists)
    {
        _cloud.ProvisioningStatus = Provisioning;

        var result = await PushAsync(inMemory, CliAgent(), HostedCloud.Target(CliSchemaName, botExists ? _cloud.SeedAgent(CliSchemaName, "Contoso Helpdesk", AuthoringShape.CliCopilot).ToString() : null), HostedCloud.Options() with { ProvisioningTimeout = TimeSpan.FromSeconds(10), PublishTimeout = TimeSpan.FromSeconds(10) });

        var reads = _cloud.ProvisioningReads.ToArray();
        Assert.True(result.Published);
        Assert.Equal(_cloud.AgentIdOf(CliSchemaName).ToString(), result.BotId);
        Assert.Equal(new[] { CliSchemaName }, _cloud.CreatedAgents);
        Assert.Equal(Enumerable.Repeat(Guid.Parse(result.BotId), 2), reads.Select(read => read.AgentId));
        AssertNothingWasWrittenBefore(reads[0]);
        Assert.NotEqual(0, reads[1].SavesSoFar);
        Assert.Equal(Seconds(1), _cloud.Clock.Delays);
        Assert.Single(_cloud.Published);
    }

    [Theory]
    [MemberData(nameof(FileModes))]
    public async Task PushAsync_OfACliAgentWithPublishOff_WritesItIntoTheBotThatStaysProvisioningWithoutWaitingOrAskingForAPublish(bool inMemory)
    {
        _cloud.ProvisioningStatus = Provisioning;

        var result = await PushAsync(inMemory, CliAgent(), HostedCloud.Target(CliSchemaName), HostedCloud.Options() with { Publish = false });

        Assert.Equal((_cloud.AgentIdOf(CliSchemaName).ToString(), false, 0), (result.BotId, result.Published, result.Diagnostics.Count));
        AssertNothingWasWrittenBefore(Assert.Single(_cloud.ProvisioningReads));
        Assert.NotEmpty(_cloud.Island.Saves);
        Assert.Empty(_cloud.Published);
        Assert.Empty(_cloud.Clock.Delays);
    }

    private void AssertNothingWasWrittenBefore(ProvisioningStatusRead read)
    {
        Assert.Equal(0, read.SavesSoFar);
        Assert.Empty(_cloud.ContentWritesAmong(read.DataverseCallsSoFar));
    }

    private static AgentWorkspaceSnapshot CliAgent() => Scaffolds.Agent("Agent", "Contoso Helpdesk", CliSchemaName, AgentAuthoringModes.CliCopilot, instructions: "Be helpful.");

    private Task<AgentPushResult> PushAsync(bool inMemory, AgentPushOptions? options = null) => PushAsync(inMemory, ClassicAgentFixtures.AgentD1("Agent D1"), HostedCloud.Target(SchemaName), options ?? HostedCloud.Options());

    private Task<AgentPushResult> PushAsync(bool inMemory, AgentWorkspaceSnapshot snapshot, AgentSyncTarget target, AgentPushOptions options) => (inMemory ? _cloud.CreateHost() : _cloud.CreateHost(new FileAccessorFactory())).PushAsync(snapshot, target, options, _authentication, CancellationToken.None);
}
