// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.Platform.Content;
using Microsoft.Agents.Platform.Content.Exceptions;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Microsoft.CopilotStudio.Sync.Hosting;
using Moq;
using Xunit;
using static Microsoft.CopilotStudio.Sync.Dataverse.SyncDataverseClient;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class AgentWorkspaceHostPushTests
{
    internal const string SchemaName = "con_contosohelpdesk";

    private const string WeatherReference = "new_sharedmsnweather_08889";

    private const string TeamsReference = "new_sharedteams_77777";

    internal static readonly Regex DeclaredDisplayName = new("^displayName:[^\r\n]*", RegexOptions.Multiline);

    private readonly HostedCloud _cloud = new();

    private readonly RecordingFileAccessorFactory _files = new();

    private readonly RecordingAuthProvider _authentication = new();

    private readonly AgentWorkspaceHost _host;

    public AgentWorkspaceHostPushTests() => _host = _cloud.CreateHost(_files);

    public static TheoryData<string, string, string?, string?> PushFindings => new()
    {
        { "connector", AgentSyncDiagnosticCodes.ConnectorRefused, "Contoso API", null },
        { "workflow", AgentSyncDiagnosticCodes.WorkflowRefused, "Rejected", "The definition is not valid." },
        { "prompt", AgentSyncDiagnosticCodes.PromptRefused, "Summarize", "The model is not available." },
    };

    public static TheoryData<string, string, string, AgentSyncDiagnostic[]> NamesTooLongToCreateTheBot => new()
    {
        { AgentAuthoringModes.CliCopilot, "Contoso Helpdesk", new string('a', 43), new[] { new AgentSyncDiagnostic(AgentSyncDiagnosticCodes.DisplayNameTooLong, "An agent display name can hold at most 42 characters, and this one holds 43.") } },
        { AgentAuthoringModes.Classic, new string('a', 43), "Contoso Helpdesk", new[] { new AgentSyncDiagnostic(AgentSyncDiagnosticCodes.DisplayNameTooLong, "An agent display name can hold at most 42 characters, and this one holds 43.", "Agent/settings.mcs.yml") } },
        { AgentAuthoringModes.CliCopilot, new string('a', 43), new string('a', 43), new[] { new AgentSyncDiagnostic(AgentSyncDiagnosticCodes.DisplayNameTooLong, "An agent display name can hold at most 42 characters, and this one holds 43.", "Agent/settings.mcs.yml") } },
        { AgentAuthoringModes.Classic, new string('a', 100), new string('b', 50), new[] { new AgentSyncDiagnostic(AgentSyncDiagnosticCodes.DisplayNameTooLong, "An agent display name can hold at most 42 characters, and this one holds 100.", "Agent/settings.mcs.yml"), new AgentSyncDiagnostic(AgentSyncDiagnosticCodes.DisplayNameTooLong, "An agent display name can hold at most 42 characters, and this one holds 50.") } },
    };

    [Fact]
    public async Task PushAsync_CreatesTheMissingBotAndLeavesTheCacheEqualToAFreshClone()
    {
        var result = await PushAsync(Scaffold(AgentAuthoringModes.CliCopilot));
        var pushedCache = _files.TakeCache("Agent");

        var clone = await CloneAsync(result.BotId);

        Assert.True(result.Published);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(new[] { SchemaName }, _cloud.CreatedAgents);
        Assert.Equal(_cloud.AgentIdOf(SchemaName).ToString(), result.BotId);
        Assert.Equal(new[] { Guid.Parse(result.BotId) }, _cloud.Published);
        Assert.Equal(pushedCache, _files.TakeCache("Agent"));
        Assert.All(clone.Snapshot.Files.Keys, path => Assert.DoesNotContain(path.Split('/'), segment => segment.Equals(".mcs", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(0, _files.LiveCount);
    }

    [Fact]
    public async Task PushAsync_ToAnExistingBot_ReusesItAndNeverCreatesAnother()
    {
        Assert.Equal(_cloud.SeedAgent(SchemaName, "Contoso Helpdesk").ToString(), (await PushAsync(Scaffold(AgentAuthoringModes.Classic))).BotId);
        Assert.Single(_cloud.CreatedAgents);
        _cloud.Cloud.Client.Verify(client => client.CreateNewAgentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<AuthoringShape>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    [Theory]
    [MemberData(nameof(NamesTooLongToCreateTheBot))]
    public async Task PushAsync_ThatMayCreateTheBotUnderADisplayNameLongerThanTheLimit_IsRefusedAsAnInvalidSnapshotBeforeAnyCloudCall(string authoringMode, string declaredDisplayName, string targetDisplayName, AgentSyncDiagnostic[] expected)
    {
        var snapshot = Declaring(Scaffold(authoringMode), declaredDisplayName);

        var failure = await Assert.ThrowsAsync<AgentWorkspaceHostException>(() => _host.PushAsync(snapshot, HostedCloud.Target(SchemaName) with { DisplayName = targetDisplayName }, HostedCloud.Options(), _authentication, CancellationToken.None));

        Assert.Equal((AgentWorkspaceHostErrorCodes.SnapshotInvalid, "The snapshot of 'Agent' cannot be pushed."), (failure.Code, failure.Message));
        Assert.Equal(expected, failure.Diagnostics);
        Assert.Empty(_cloud.Cloud.Client.Invocations);
        Assert.Empty(_authentication.Audiences);
        Assert.Empty(_files.Opened);
    }

    [Theory]
    [InlineData(AgentAuthoringModes.Classic)]
    [InlineData(AgentAuthoringModes.CliCopilot)]
    public async Task PushAsync_CreatingTheBotUnderADisplayNameOfTheLimit_CreatesItUnderThatName(string authoringMode)
    {
        var displayName = new string('a', 42);

        var result = await _host.PushAsync(Declaring(Scaffold(authoringMode), displayName), HostedCloud.Target(SchemaName) with { DisplayName = displayName }, HostedCloud.Options(), _authentication, CancellationToken.None);

        Assert.Equal((true, 0), (result.Published, result.Diagnostics.Count));
        Assert.Equal(new[] { SchemaName }, _cloud.CreatedAgents);
        Assert.Equal(displayName, _cloud.Island.EntityOf(Guid.Parse(result.BotId))!.DisplayName);
    }

    [Theory]
    [InlineData(AgentAuthoringModes.Classic, AuthoringShape.Classic, true)]
    [InlineData(AgentAuthoringModes.CliCopilot, AuthoringShape.CliCopilot, true)]
    [InlineData(AgentAuthoringModes.CliCopilot, AuthoringShape.CliCopilot, false)]
    public async Task PushAsync_ToABotThatAlreadyCarriesADisplayNameLongerThanTheLimit_WritesItWithTheNameUnchangedAndNoDiagnostic(string authoringMode, AuthoringShape shape, bool targetsTheBotById)
    {
        var legacyName = new string('L', 50);
        var botId = _cloud.SeedAgent(SchemaName, legacyName, shape);

        var result = await _host.PushAsync(Declaring(Scaffold(authoringMode), legacyName), HostedCloud.Target(SchemaName, targetsTheBotById ? botId.ToString() : null), HostedCloud.Options(createIfMissing: targetsTheBotById), _authentication, CancellationToken.None);

        Assert.Equal((botId.ToString(), true, 0), (result.BotId, result.Published, result.Diagnostics.Count));
        Assert.Equal(legacyName, Assert.Single(_cloud.Island.SavedChanges, changes => changes.Bot is not null).Bot!.DisplayName);
        Assert.Equal(legacyName, _cloud.Island.EntityOf(botId)!.DisplayName);
    }

    [Fact]
    public async Task PushAsync_RetriedAfterAFailure_NeverCreatesASecondBot()
    {
        var snapshot = Scaffold(AgentAuthoringModes.CliCopilot);
        _cloud.Island.SaveFailures.Enqueue(new InvalidOperationException("The components could not be saved."));

        var failure = await Assert.ThrowsAsync<AgentWorkspaceHostException>(() => PushAsync(snapshot));
        var retried = await PushAsync(snapshot);

        Assert.Equal(AgentWorkspaceHostErrorCodes.PushFailed, failure.Code);
        Assert.Equal(new[] { SchemaName }, _cloud.CreatedAgents);
        Assert.Equal(_cloud.AgentIdOf(SchemaName).ToString(), retried.BotId);
        Assert.Equal(0, _files.LiveCount);
    }

    [Fact]
    public async Task PushAsync_WithoutCreateIfMissing_RefusesAMissingBotBeforeAnyChange()
    {
        Assert.Equal(AgentWorkspaceHostErrorCodes.BotNotFound, (await Assert.ThrowsAsync<AgentWorkspaceHostException>(async () => await PushAsync(Scaffold(AgentAuthoringModes.CliCopilot), HostedCloud.Options(createIfMissing: false)))).Code);
        Assert.Empty(_cloud.CreatedAgents);
        Assert.Empty(_cloud.Island.Saves);
        Assert.Empty(_cloud.Published);
        Assert.Equal(0, _files.LiveCount);
    }

    [Fact]
    public async Task PushAsync_WhenTheSchemaNameBelongsToAnotherBot_RefusesBeforeAnyChange()
    {
        _cloud.SeedAgent(SchemaName, "Contoso Helpdesk");

        Assert.Equal(AgentWorkspaceHostErrorCodes.BotNotFound, (await Assert.ThrowsAsync<AgentWorkspaceHostException>(async () => await _host.PushAsync(Scaffold(AgentAuthoringModes.CliCopilot), HostedCloud.Target(SchemaName, Guid.NewGuid().ToString()), HostedCloud.Options(), _authentication, CancellationToken.None))).Code);
        Assert.Empty(_cloud.Island.Saves);
        Assert.Empty(_cloud.Published);
    }

    [Fact]
    public async Task PushAsync_OverwritesRemoteEditsWithoutADriftCheck()
    {
        var botId = _cloud.SeedAgent(SchemaName, "Contoso Helpdesk", PushComponentCollectionExclusionTests.Topic($"{SchemaName}.topic.Greeting", "Edited In The Portal"), PushComponentCollectionExclusionTests.Topic($"{SchemaName}.topic.RemoteOnly", "Remote Only"));

        await PushAsync(Scaffold(AgentAuthoringModes.Classic));

        var components = _cloud.Island.ComponentsOf(botId);
        Assert.DoesNotContain(components, component => component.SchemaNameString == $"{SchemaName}.topic.RemoteOnly");
        Assert.Equal("Greeting", Assert.Single(components, component => component.SchemaNameString == $"{SchemaName}.topic.Greeting").DisplayName);
    }

    [Fact]
    public async Task PushAsync_WhenTheBotChangesInTheCloudBetweenAttachAndPush_ReadsItAgainAndPushesOverTheChange()
    {
        var greeting = PushComponentCollectionExclusionTests.Topic($"{SchemaName}.topic.Greeting", "Before The Deploy");
        var botId = _cloud.SeedAgent(SchemaName, "Contoso Helpdesk", greeting);
        var snapshot = Scaffold(AgentAuthoringModes.Classic);
        ChangeInTheCloudAfterItsFirstRead(botId, greeting);

        var result = await PushAsync(snapshot);
        var pushedCache = _files.TakeCache("Agent");
        await CloneAsync(result.BotId);

        Assert.True(result.Published);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(botId.ToString(), result.BotId);
        Assert.Equal("Greeting", Assert.Single(_cloud.Island.ComponentsOf(botId), component => component.SchemaNameString == greeting.SchemaNameString).DisplayName);
        Assert.Equal(2, SaveRequestsFor(botId));
        Assert.Equal(new[] { botId }, _cloud.Published);
        Assert.Equal(pushedCache, _files.TakeCache("Agent"));
    }

    [Fact]
    public async Task PushAsync_WhenACollectionChangesInTheCloudBetweenItsReadAndItsPush_ReadsItAgainAndPushesOverTheChange()
    {
        var botId = _cloud.SeedAgent(SchemaName, "Contoso Helpdesk");
        var lookup = PushComponentCollectionExclusionTests.Topic("cree9_tools.topic.Lookup", "Before The Deploy");
        var toolsId = _cloud.SeedCollection("cree9_tools", "Shared Tools", lookup);
        var snapshot = ScaffoldWithCollections();
        ChangeInTheCloudAfterItsFirstRead(toolsId, lookup);

        var result = await PushAsync(snapshot);

        Assert.True(result.Published);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(botId.ToString(), result.BotId);
        Assert.Equal("Lookup", Assert.Single(_cloud.Island.ComponentsOf(toolsId)).DisplayName);
        Assert.Equal(2, SaveRequestsFor(toolsId));
        Assert.Equal(1, SaveRequestsFor(botId));
        Assert.Equal(new[] { botId }, _cloud.Published);
    }

    [Fact]
    public async Task PushAsync_WhenTheBotStillChangesInTheCloudAfterTheReread_FailsWithoutPublishing()
    {
        var botId = _cloud.SeedAgent(SchemaName, "Contoso Helpdesk", PushComponentCollectionExclusionTests.Topic($"{SchemaName}.topic.Greeting", "Before The Deploy"));
        var snapshot = Scaffold(AgentAuthoringModes.Classic);
        for (var save = 0; save < 4; save++)
        {
            _cloud.Island.SaveFailures.Enqueue(PushConcurrencyRetryTests.MakeConcurrencyException());
        }

        var failure = await Assert.ThrowsAsync<AgentWorkspaceHostException>(() => PushAsync(snapshot));

        Assert.Equal(AgentWorkspaceHostErrorCodes.PushFailed, failure.Code);
        Assert.IsType<DataversePreconditionFailedException>(failure.InnerException);
        Assert.Equal(4, SaveRequestsFor(botId));
        Assert.Empty(_cloud.Published);
        Assert.Equal(0, _files.LiveCount);
    }

    [Fact]
    public async Task PushAsync_WhenPublishFails_ReportsItAndKeepsThePushedContent()
    {
        _cloud.PublishThrows = new InvalidOperationException("The environment refused to publish.");

        var result = await PushAsync(Scaffold(AgentAuthoringModes.Classic));

        Assert.False(result.Published);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(AgentSyncDiagnosticCodes.PublishFailed, diagnostic.Code);
        Assert.Contains("The environment refused to publish.", diagnostic.Message, StringComparison.Ordinal);
        Assert.NotEmpty(_cloud.Island.ComponentsOf(Guid.Parse(result.BotId)));
    }

    [Fact]
    public async Task PushAsync_ReportsThePublishOnlyOnceCopilotStudioFinishedANewOne()
    {
        var result = await PushAsync(Scaffold(AgentAuthoringModes.Classic));

        Assert.True(result.Published);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(new[] { nameof(IAgentPublishDataverseClient.GetLastPublishOperationAsync), nameof(IAgentPublishDataverseClient.PublishAgentAsync), nameof(IAgentPublishDataverseClient.GetLastPublishOperationAsync) }, _cloud.Publisher.Invocations.Select(invocation => invocation.Method.Name).Where(name => name is nameof(IAgentPublishDataverseClient.GetLastPublishOperationAsync) or nameof(IAgentPublishDataverseClient.PublishAgentAsync)));
        Assert.Equal(new[] { TimeSpan.FromSeconds(1) }, _cloud.Clock.Delays);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PushAsync_AsksForThePublishOnlyWhenTheOptionsSaySoAndPushesTheContentEitherWay(bool publish)
    {
        var result = await PushAsync(Scaffold(AgentAuthoringModes.Classic), HostedCloud.Options() with { Publish = publish });

        Assert.Equal((publish, 0), (result.Published, result.Diagnostics.Count));
        Assert.Equal(publish ? new[] { Guid.Parse(result.BotId) } : Array.Empty<Guid>(), _cloud.Published);
        Assert.Equal(publish ? 3 : 0, _cloud.Publisher.Invocations.Count(invocation => invocation.Method.Name is nameof(IAgentPublishDataverseClient.GetLastPublishOperationAsync) or nameof(IAgentPublishDataverseClient.PublishAgentAsync)));
        Assert.Equal(publish ? new[] { TimeSpan.FromSeconds(1) } : Array.Empty<TimeSpan>(), _cloud.Clock.Delays);
        Assert.NotEmpty(_cloud.Island.ComponentsOf(Guid.Parse(result.BotId)));
    }

    [Fact]
    public async Task PushAsync_WithPublishOff_ReportsNothingAboutAPublishThatWouldHaveFailed()
    {
        _cloud.PublishThrows = new InvalidOperationException("The environment refused to publish.");

        var result = await PushAsync(Scaffold(AgentAuthoringModes.Classic), HostedCloud.Options() with { Publish = false });

        Assert.Equal((false, 0), (result.Published, result.Diagnostics.Count));
        Assert.NotEmpty(_cloud.Island.ComponentsOf(Guid.Parse(result.BotId)));
    }

    [Fact]
    public async Task PushAsync_WhenTheFirstPublishRequestIsRefused_AsksOnceMoreAndReportsThePublish()
    {
        _cloud.PublishRefusals.Enqueue(new DataverseRequestException(HttpStatusCode.BadRequest, "{\"statuscode\":409,\"statusdescription\":\"Conflict\"}"));

        var result = await PushAsync(Scaffold(AgentAuthoringModes.Classic));

        Assert.True(result.Published);
        Assert.Empty(result.Diagnostics);
        Assert.Single(_cloud.Published);
        Assert.Equal(new[] { AgentPublishService.PublishRetryDelay, TimeSpan.FromSeconds(1) }, _cloud.Clock.Delays);
        _cloud.Publisher.Verify(publisher => publisher.GetLastPublishOperationAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [Fact]
    public async Task PushAsync_WhenBothPublishRequestsAreRefused_ReportsTheSecondRefusal()
    {
        _cloud.PublishRefusals.Enqueue(new DataverseRequestException(HttpStatusCode.BadRequest, "first refusal"));
        _cloud.PublishRefusals.Enqueue(new DataverseRequestException(HttpStatusCode.BadRequest, "second refusal"));

        var result = await PushAsync(Scaffold(AgentAuthoringModes.Classic));

        Assert.False(result.Published);
        Assert.Equal(new AgentSyncDiagnostic(AgentSyncDiagnosticCodes.PublishFailed, "The bot was pushed but could not be published: HTTP 400 Bad Request."), Assert.Single(result.Diagnostics));
        Assert.Empty(_cloud.Published);
    }

    [Fact]
    public async Task PushAsync_WhenCopilotStudioFinishesThePublishWithAnotherStatus_ReportsThatStatus()
    {
        _cloud.PublishFinishesWith = "Failed";

        var result = await PushAsync(Scaffold(AgentAuthoringModes.Classic));

        Assert.False(result.Published);
        Assert.Equal(new AgentSyncDiagnostic(AgentSyncDiagnosticCodes.PublishFailed, "Copilot Studio finished publishing the bot with status 'Failed'."), Assert.Single(result.Diagnostics));
        Assert.NotEmpty(_cloud.Island.ComponentsOf(Guid.Parse(result.BotId)));
    }

    [Fact]
    public async Task PushAsync_WhenCopilotStudioDoesNotFinishThePublishInTime_ReportsThatItMayStillComplete()
    {
        _cloud.PublishFinishesWith = null;

        var result = await PushAsync(Scaffold(AgentAuthoringModes.Classic), HostedCloud.Options() with { PublishTimeout = TimeSpan.FromSeconds(10) });

        Assert.False(result.Published);
        Assert.Equal(new AgentSyncDiagnostic(AgentSyncDiagnosticCodes.PublishTimedOut, "Copilot Studio did not finish publishing the bot within 00:00:10; the publish may still complete. Deploy again to confirm."), Assert.Single(result.Diagnostics));
        Assert.Equal(Enumerable.Repeat(TimeSpan.FromSeconds(1), 10), _cloud.Clock.Delays);
        Assert.Single(_cloud.Published);
    }

    [Fact]
    public async Task PushAsync_WhenThePublishProgressCannotBeRead_ReportsWhyWithoutTheServiceResponse()
    {
        _cloud.PublishProgressThrows = new DataverseRequestException(HttpStatusCode.Forbidden, "secret detail");

        var result = await PushAsync(Scaffold(AgentAuthoringModes.Classic));

        Assert.False(result.Published);
        Assert.Equal(new AgentSyncDiagnostic(AgentSyncDiagnosticCodes.PublishFailed, "Copilot Studio was asked to publish the bot, but its progress could not be read. The request for the publish's progress failed with HTTP 403 Forbidden."), Assert.Single(result.Diagnostics));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task PushAsync_WithAPublishTimeoutThatIsNotPositive_IsRefusedBeforeAnyCloudCall(int timeoutSeconds)
    {
        var snapshot = Scaffold(AgentAuthoringModes.Classic);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => PushAsync(snapshot, HostedCloud.Options() with { PublishTimeout = TimeSpan.FromSeconds(timeoutSeconds) }));

        Assert.Empty(_cloud.Cloud.Client.Invocations);
        Assert.Empty(_files.Opened);
    }

    [Fact]
    public async Task PushAsync_ReportsTheFlowsLeftInDraftWithTheConnectionReferencesTheTargetLeavesUnbound()
    {
        var weatherId = Guid.NewGuid();
        var notifyId = Guid.NewGuid();
        var snapshot = Scaffold(AgentAuthoringModes.CliCopilot);
        snapshot = With(snapshot, $"Agent/infrastructure/connections/{WeatherReference}.sync.yaml", Declaration(WeatherReference, "shared_msnweather"));
        snapshot = With(snapshot, $"Agent/infrastructure/connections/{TeamsReference}.sync.yaml", Declaration(TeamsReference, "shared_teams"));
        snapshot = WithWorkflow(snapshot, "Weather", weatherId, Definition("shared_msnweather", WeatherReference));
        snapshot = WithWorkflow(snapshot, "Notify", notifyId, Definition("shared_teams", TeamsReference));
        _cloud.Cloud.AddConnectionReference(WeatherReference, "/providers/Microsoft.PowerApps/apis/shared_msnweather", "conn-weather");

        var result = await PushAsync(snapshot);
        var pushedCache = _files.TakeCache("Agent");
        await CloneAsync(result.BotId);

        Assert.True(result.Published);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(pushedCache, _files.TakeCache("Agent"));
        Assert.Equal("conn-weather", _cloud.Cloud.ConnectionReferences[WeatherReference].ConnectionId);
        Assert.Equal(1, _cloud.Cloud.Workflows[weatherId].StateCode);
        Assert.Equal(0, _cloud.Cloud.Workflows[notifyId].StateCode);
        var draft = Assert.Single(result.FlowsLeftInDraft);
        Assert.Equal((notifyId.ToString(), "Notify"), (draft.WorkflowId, draft.Name));
        Assert.Equal(new[] { TeamsReference }, draft.UnboundConnectionReferences);
    }

    [Fact]
    public async Task PushAsync_WritesAFlowWhoseConnectionsTheTargetBindsActiveFromTheStart()
    {
        var weatherId = Guid.NewGuid();
        var plainId = Guid.NewGuid();
        var snapshot = With(Scaffold(AgentAuthoringModes.CliCopilot), $"Agent/infrastructure/connections/{WeatherReference}.sync.yaml", Declaration(WeatherReference, "shared_msnweather"));
        snapshot = WithWorkflow(WithWorkflow(snapshot, "Weather", weatherId, Definition("shared_msnweather", WeatherReference)), "Plain", plainId, "{}");
        _cloud.Cloud.AddConnectionReference(WeatherReference, "/providers/Microsoft.PowerApps/apis/shared_msnweather", "conn-weather");

        var result = await PushAsync(snapshot);

        var written = _cloud.Cloud.Client.Invocations.Where(invocation => invocation.Method.Name == nameof(ISyncDataverseClient.UpdateWorkflowAsync)).Select(invocation => (WorkflowMetadata)invocation.Arguments[1]!).ToArray();
        Assert.Equal(new[] { (plainId, true), (weatherId, true) }.OrderBy(workflow => workflow.Item1), written.Select(workflow => (workflow.WorkflowId, workflow.StateCode == 1)).OrderBy(workflow => workflow.WorkflowId));
        Assert.DoesNotContain(_cloud.Cloud.Client.Invocations, invocation => invocation.Method.Name == nameof(ISyncDataverseClient.SetWorkflowStateAsync) && Equals(invocation.Arguments[1], false));
        Assert.Empty(result.FlowsLeftInDraft);
        Assert.Equal((1, 1), (_cloud.Cloud.Workflows[weatherId].StateCode, _cloud.Cloud.Workflows[plainId].StateCode));
    }

    [Fact]
    public async Task PushAsync_WhenTheServicesReplyCannotBeRead_WaitsOnTheHostsClockAndSendsTheComponentsAgain()
    {
        _cloud.Island.SaveFailures.Enqueue(new ArgumentException("Improper response, not implemented"));

        var result = await PushAsync(Scaffold(AgentAuthoringModes.Classic));
        var pushedCache = _files.TakeCache("Agent");
        await CloneAsync(result.BotId);

        Assert.True(result.Published);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1) }, _cloud.Clock.Delays);
        Assert.Equal(2, _cloud.Island.Service.Invocations.Count(invocation => invocation.Method.Name == nameof(IIslandControlPlaneService.SaveChangesAsync)));
        Assert.Equal(pushedCache, _files.TakeCache("Agent"));
    }

    [Fact]
    public async Task PushAsync_WhenTheServicesReplyCannotBeReadOnAnyAttempt_FailsAfterFourAttempts()
    {
        for (var attempt = 0; attempt < WorkspacePushService.UnreadableReplyAttempts; attempt++)
        {
            _cloud.Island.SaveFailures.Enqueue(new ArgumentException("Improper response, not implemented"));
        }

        var failure = await Assert.ThrowsAsync<AgentWorkspaceHostException>(async () => await PushAsync(Scaffold(AgentAuthoringModes.Classic)));

        Assert.Equal((AgentWorkspaceHostErrorCodes.PushFailed, "The service's reply to the push could not be read after 4 attempt(s)."), (failure.Code, failure.Message));
        Assert.Equal(WorkspacePushErrorCodes.ResponseUnreadable, Assert.IsType<WorkspacePushException>(failure.InnerException).Code);
        Assert.Equal(new[] { 2, 4, 6 }.Select(seconds => TimeSpan.FromSeconds(seconds)), _cloud.Clock.Delays);
        Assert.Empty(_cloud.Published);
        Assert.Equal(0, _files.LiveCount);
    }

    [Fact]
    public async Task PushAsync_PushesEveryDeclaredCollectionBeforeTheAgentAndReportsTheSharedOnes()
    {
        var otherBotId = _cloud.SeedAgent("con_other", "Other Agent");
        var toolsId = _cloud.SeedCollection("cree9_tools", "Shared Tools", PushComponentCollectionExclusionTests.Topic("cree9_tools.topic.Stale", "Stale"));
        _cloud.Link(otherBotId, toolsId);

        var result = await PushAsync(ScaffoldWithCollections());
        var pushedCaches = new[] { _files.TakeCache("Agent"), _files.TakeCache("Shared Tools"), _files.TakeCache("Zeta Tools") };
        var botId = Guid.Parse(result.BotId);
        var zetaId = _cloud.CollectionIdOf("cree9_zeta");

        await _host.CloneAsync(new AgentCloneRequest(HostedCloud.Target(SchemaName, result.BotId), "Agent", new Dictionary<string, string> { ["cree9_tools"] = "Shared Tools", ["cree9_zeta"] = "Zeta Tools" }, Array.Empty<string>()), _authentication, CancellationToken.None);

        Assert.Equal(new[] { toolsId, zetaId, botId }, _cloud.Island.Saves.Select(FakeIsland.IdentityOf).Distinct());
        Assert.Equal(new[] { "cree9_tools.topic.Lookup" }, _cloud.Island.ComponentsOf(toolsId).Select(component => component.SchemaNameString));
        Assert.Equal(new[] { "cree9_zeta.topic.Escalate" }, _cloud.Island.ComponentsOf(zetaId).Select(component => component.SchemaNameString));
        var shared = Assert.Single(result.SharedCollections);
        Assert.Equal("cree9_tools", shared.SchemaName);
        Assert.Equal(new[] { "con_other" }, shared.OtherBotSchemaNames);
        Assert.Equal(new[] { otherBotId, botId }, await _cloud.Collections.Object.GetAgentIdsForComponentCollectionAsync(toolsId, CancellationToken.None));
        Assert.Equal(new[] { botId }, await _cloud.Collections.Object.GetAgentIdsForComponentCollectionAsync(zetaId, CancellationToken.None));
        _cloud.Links.Verify(links => links.GetComponentCollectionsBySchemaNamesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Once());
        Assert.Equal(pushedCaches, new[] { _files.TakeCache("Agent"), _files.TakeCache("Shared Tools"), _files.TakeCache("Zeta Tools") });
    }

    [Fact]
    public async Task PushAsync_WhenCanceledBetweenSteps_StopsBeforeTheContentAndReleasesTheSession()
    {
        using var cancellation = new CancellationTokenSource();
        var snapshot = WithWorkflow(Scaffold(AgentAuthoringModes.CliCopilot), "Weather", Guid.NewGuid(), "{}");
        _cloud.RefusesWorkflow = _ =>
        {
            cancellation.Cancel();
            return null;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PushAsync(snapshot, cancellationToken: cancellation.Token));

        Assert.Empty(_cloud.Island.Saves);
        Assert.Empty(_cloud.Published);
        Assert.Equal(0, _files.LiveCount);
    }

    [Fact]
    public async Task PushAsync_WithACanceledToken_TouchesNoEnvironment()
    {
        var snapshot = Scaffold(AgentAuthoringModes.CliCopilot);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PushAsync(snapshot, cancellationToken: new CancellationToken(canceled: true)));

        Assert.Empty(_cloud.Cloud.Client.Invocations);
        Assert.Empty(_files.Opened);
    }

    [Fact]
    public async Task PushAsync_WhenTheCloudNormalizesAndRejectsPartOfThePush_LeavesTheCacheEqualToAFreshClone()
    {
        _cloud.Island.Normalizes = component => component.SchemaNameString.EndsWith(".topic.Greeting", StringComparison.Ordinal) ? WithDescription(component, "Normalized by the service.") : component;
        _cloud.Island.Rejects = component => component.SchemaNameString.EndsWith(".topic.Search", StringComparison.Ordinal);
        _cloud.RefusesWorkflow = workflow => workflow.Name == "Rejected" ? "The definition is not valid." : null;

        var result = await PushAsync(WithWorkflow(WithWorkflow(Scaffold(AgentAuthoringModes.Classic), "Accepted", Guid.NewGuid(), "{}"), "Rejected", Guid.NewGuid(), "{}"));
        var pushedCache = _files.TakeCache("Agent");
        await CloneAsync(result.BotId);

        Assert.True(result.Published);
        Assert.Equal(new[] { (AgentSyncDiagnosticCodes.WorkflowRefused, "The environment refused the workflow 'Rejected': The definition is not valid.") }, result.Diagnostics.Select(diagnostic => (diagnostic.Code, diagnostic.Message)));
        Assert.Contains("Normalized by the service.", pushedCache, StringComparison.Ordinal);
        Assert.DoesNotContain(".topic.Search", pushedCache, StringComparison.Ordinal);
        Assert.Contains("Accepted", pushedCache, StringComparison.Ordinal);
        Assert.DoesNotContain("Rejected", pushedCache, StringComparison.Ordinal);
        Assert.Equal(pushedCache, _files.TakeCache("Agent"));
    }

    [Fact]
    public async Task PushAsync_OfACloneFromAnotherEnvironment_LeavesTheCacheEqualToAFreshClone()
    {
        var result = await PushAsync(await CloneFromAnotherEnvironmentAsync());
        var pushedCache = _files.TakeCache("Agent");
        await CloneAsync(result.BotId);

        Assert.True(result.Published);
        Assert.Contains($"{SchemaName}.topic.Greeting", pushedCache, StringComparison.Ordinal);
        Assert.Contains("Weather", pushedCache, StringComparison.Ordinal);
        Assert.Equal(pushedCache, _files.TakeCache("Agent"));
    }

    [Theory]
    [MemberData(nameof(PushFindings))]
    public async Task PushAsync_ReportsWhatEachPushFindingConcernsAndTheReasonTheServiceGave(string finding, string code, string? subject, string? detail)
    {
        var reported = Assert.Single((await PushAsync(await ArrangeFindingAsync(finding))).Diagnostics, diagnostic => diagnostic.Code == code);
        Assert.Equal(subject, reported.Subject);
        Assert.Equal(detail, reported.Detail);
    }

    [Theory]
    [InlineData(false, "Weather")]
    [InlineData(true, null)]
    public async Task PushAsync_WhenTheEnvironmentKeepsFlowsFromStarting_NamesTheFlowOnlyWhenTheRefusalConcernsOne(bool plainRefusedToo, string? subject)
    {
        var weatherId = Guid.NewGuid();
        var plainId = Guid.NewGuid();
        var snapshot = WithWorkflow(WithWorkflow(Scaffold(AgentAuthoringModes.CliCopilot), "Weather", weatherId, "{}"), "Plain", plainId, "{}");
        _cloud.RefusesActivation = workflowId => workflowId == weatherId || (plainRefusedToo && workflowId == plainId) ? new DataverseRequestException(HttpStatusCode.BadRequest, "{\"error\":{\"code\":\"ConnectionAuthorizationFailed\",\"message\":\"The connection cannot be used.\"}}") : null;

        var result = await PushAsync(snapshot);

        var refusal = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == AgentSyncDiagnosticCodes.ActivationRefused);
        Assert.Equal(subject, refusal.Subject);
        Assert.Null(refusal.Detail);
        Assert.True(result.Published);
    }

    [Fact]
    public async Task PushAsync_WithASnapshotOfAnotherAuthoringShape_RefusesBeforeAnyCloudCall()
    {
        var snapshot = Scaffold(AgentAuthoringModes.CliCopilot);

        var failure = await Assert.ThrowsAsync<AgentWorkspaceHostException>(() => _host.PushAsync(snapshot, HostedCloud.Target(SchemaName, authoringShape: AgentAuthoringModes.Classic), HostedCloud.Options(), _authentication, CancellationToken.None));

        Assert.Equal(AgentWorkspaceHostErrorCodes.SnapshotInvalid, failure.Code);
        Assert.Equal(AgentSyncDiagnosticCodes.AuthoringShapeMismatch, Assert.Single(failure.Diagnostics).Code);
        Assert.Empty(_cloud.Cloud.Client.Invocations);
    }

    [Theory]
    [InlineData("Agent/.mcs/botdefinition.json", AgentSyncDiagnosticCodes.SyncStateInPath)]
    [InlineData("Agent/topics/CON.mcs.yml", AgentSyncDiagnosticCodes.ReservedDeviceName)]
    [InlineData("Agent/com\u00B9/Extra.mcs.yml", AgentSyncDiagnosticCodes.ReservedDeviceName)]
    [InlineData("Agent/topics/Extra?.mcs.yml", AgentSyncDiagnosticCodes.UnsafePath)]
    public async Task PushAsync_WithAnInvalidSnapshot_RefusesBeforeAnyCloudCall(string path, string code)
    {
        var snapshot = With(Scaffold(AgentAuthoringModes.CliCopilot), path, "{}");

        var failure = await Assert.ThrowsAsync<AgentWorkspaceHostException>(() => PushAsync(snapshot));

        Assert.Equal(AgentWorkspaceHostErrorCodes.SnapshotInvalid, failure.Code);
        Assert.Equal((code, path), (Assert.Single(failure.Diagnostics).Code, failure.Diagnostics[0].Path));
        Assert.Empty(_cloud.Cloud.Client.Invocations);
        Assert.Empty(_files.Opened);
    }

    internal static AgentWorkspaceSnapshot With(AgentWorkspaceSnapshot snapshot, string path, string? content) => new(snapshot.Folder, snapshot.Files.Where(file => file.Key != path).Concat(content is null ? Array.Empty<KeyValuePair<string, ReadOnlyMemory<byte>>>() : new[] { new KeyValuePair<string, ReadOnlyMemory<byte>>(path, Encoding.UTF8.GetBytes(content)) }).ToDictionary(file => file.Key, file => file.Value));

    internal static string Text(AgentWorkspaceSnapshot snapshot, string path) => Encoding.UTF8.GetString(snapshot.Files[path].Span);

    internal static AgentWorkspaceSnapshot Declaring(AgentWorkspaceSnapshot snapshot, string displayName) => With(snapshot, "Agent/settings.mcs.yml", DeclaredDisplayName.Replace(Text(snapshot, "Agent/settings.mcs.yml"), "displayName: " + displayName));

    internal static AgentWorkspaceSnapshot WithWorkflow(AgentWorkspaceSnapshot snapshot, string name, Guid workflowId, string definition, int stateCode = 1)
    {
        var folder = $"{snapshot.Folder}/{WorkflowWorkspace.RelativeFolderFor(name, workflowId)}";

        return With(With(snapshot, $"{folder}/{WorkflowWorkspace.DefinitionFileName}", definition), $"{folder}/{WorkflowWorkspace.MetadataFileName}", WorkflowMetadataFile.Write(new WorkflowMetadata { WorkflowId = workflowId, Name = name, StateCode = stateCode, StatusCode = stateCode == 1 ? 2 : 1 }));
    }

    internal static AgentWorkspaceSnapshot WithConnector(AgentWorkspaceSnapshot snapshot, Guid connectorId)
    {
        var folder = $"{snapshot.Folder}/connectors/ContosoApi-{connectorId}";

        return With(With(snapshot, $"{folder}/metadata.yml", $"{{\"connectorid\":\"{connectorId}\",\"name\":\"ContosoApi\",\"displayname\":\"Contoso API\",\"connectorinternalid\":\"shared_contosoapi\",\"openapidefinition\":\"connectors/ContosoApi-{connectorId}/openapidefinition.json\",\"connectortype\":0}}"), $"{folder}/openapidefinition.json", "{\"swagger\":\"2.0\"}");
    }

    internal static string Declaration(string logicalName, string connector) => $"connectionReferences:\n  - connectionReferenceLogicalName: {logicalName}\n    connectorId: /providers/Microsoft.PowerApps/apis/{connector}\n";

    internal static string Definition(string connector, string logicalName) => "{\"properties\":{\"connectionReferences\":{\"" + connector + "\":{\"api\":{\"name\":\"" + connector + "\"},\"connection\":{\"connectionReferenceLogicalName\":\"" + logicalName + "\"}}}}}";

    internal static string Topic(string name) => $"mcs.metadata:\n  componentName: {name}\nkind: AdaptiveDialog\nbeginDialog:\n  kind: OnRecognizedIntent\n  id: main\n  intent:\n    displayName: {name}\n    triggerQueries:\n      - {name} please\n\n  actions:\n    - kind: SendActivity\n      id: sendMessage_{name}\n      activity:\n        text:\n          - {name} it is.\n";

    internal static AgentWorkspaceSnapshot ScaffoldWithCollections()
    {
        var snapshot = Scaffolds.Agent("Agent", "Contoso Helpdesk", SchemaName, AgentAuthoringModes.Classic, instructions: "Be helpful.");
        snapshot = With(snapshot, "Agent/references.mcs.yml", "componentCollections:\n  - schemaName: cree9_tools\n    directory: ../Shared Tools/\n  - schemaName: cree9_zeta\n    directory: ../Zeta Tools/\n");
        snapshot = With(snapshot, "Shared Tools/collection.mcs.yml", "schemaName: cree9_tools\ndisplayName: Shared Tools\n");
        snapshot = With(snapshot, "Shared Tools/topics/Lookup.mcs.yml", Topic("Lookup"));
        snapshot = With(snapshot, "Zeta Tools/collection.mcs.yml", "schemaName: cree9_zeta\ndisplayName: Zeta Tools\n");

        return With(snapshot, "Zeta Tools/topics/Escalate.mcs.yml", Topic("Escalate"));
    }

    private static BotComponentBase WithDescription(BotComponentBase component, string description)
    {
        var builder = component.ToBuilder();
        builder.Description = description;

        return builder.Build();
    }

    private async Task<AgentWorkspaceSnapshot> CloneFromAnotherEnvironmentAsync()
    {
        var source = new HostedCloud();
        var sourceBotId = source.SeedAgent("con_source", "Contoso Helpdesk", PushComponentCollectionExclusionTests.Topic("con_source.topic.Greeting", "Greeting"));
        source.AddWorkflow(sourceBotId, new WorkflowMetadata { WorkflowId = Guid.NewGuid(), Name = "Weather", ClientData = Definition("shared_msnweather", WeatherReference), StateCode = 0, StatusCode = 1 });
        source.AddPrompt(sourceBotId, new AIPromptMetadata { AIModelId = Guid.NewGuid(), Name = "Summarize", CustomConfiguration = "{\"version\":\"1\"}" });

        return (await source.CreateHost().CloneAsync(new AgentCloneRequest(new AgentSyncTarget(HostedCloud.Organization, "env-source", HostedCloud.TenantId, sourceBotId.ToString(), "con_source", "Contoso Helpdesk", string.Empty), "Agent", new Dictionary<string, string>(), Array.Empty<string>()), _authentication, CancellationToken.None)).Snapshot;
    }

    private async Task<AgentWorkspaceSnapshot> ArrangeFindingAsync(string finding)
    {
        if (finding == "connector")
        {
            _cloud.Cloud.Client.Setup(client => client.UpsertConnectorAsync(It.IsAny<CustomConnectorMetadata>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("The definition is not valid."));
            return WithConnector(Scaffold(AgentAuthoringModes.Classic), Guid.NewGuid());
        }

        if (finding == "workflow")
        {
            _cloud.RefusesWorkflow = workflow => workflow.Name == "Rejected" ? "The definition is not valid." : null;
            return WithWorkflow(Scaffold(AgentAuthoringModes.Classic), "Rejected", Guid.NewGuid(), "{}");
        }

        _cloud.RefusesPrompt = prompt => prompt.Name == "Summarize" ? "The model is not available." : null;
        return await CloneFromAnotherEnvironmentAsync();
    }

    private void ChangeInTheCloudAfterItsFirstRead(Guid identity, BotComponentBase component)
    {
        _cloud.Island.Pauses = context =>
        {
            if (FakeIsland.IdentityOf(context) == identity)
            {
                _cloud.Island.Pauses = null;
                _cloud.Island.Seed(identity, PushComponentCollectionExclusionTests.Topic(component.SchemaNameString, "Edited During The Deploy").WithId(component.Id.Value).WithVersion(component.Version + 1));
                _cloud.Island.SaveFailures.Enqueue(PushConcurrencyRetryTests.MakeConcurrencyException());
            }

            return Task.CompletedTask;
        };
    }

    private int SaveRequestsFor(Guid identity) => _cloud.Island.Service.Invocations.Count(invocation => invocation.Method.Name == nameof(IIslandControlPlaneService.SaveChangesAsync) && FakeIsland.IdentityOf((AuthoringOperationContextBase)invocation.Arguments[0]!) == identity);

    private static AgentWorkspaceSnapshot Scaffold(string authoringMode) => Scaffolds.Agent("Agent", "Contoso Helpdesk", SchemaName, authoringMode, instructions: "Be helpful.");

    private Task<AgentPushResult> PushAsync(AgentWorkspaceSnapshot snapshot, AgentPushOptions? options = null, CancellationToken cancellationToken = default) => _host.PushAsync(snapshot, HostedCloud.Target(SchemaName), options ?? HostedCloud.Options(), _authentication, cancellationToken);

    private Task<AgentCloneResult> CloneAsync(string botId) => _host.CloneAsync(new AgentCloneRequest(HostedCloud.Target(SchemaName, botId), "Agent", new Dictionary<string, string>(), Array.Empty<string>()), _authentication, CancellationToken.None);
}
