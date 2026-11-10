// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Microsoft.CopilotStudio.Sync.Hosting;
using Moq;
using Xunit;
using static Microsoft.CopilotStudio.Sync.UnitTests.AgentPublishServiceTests;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public sealed class LocalModeProvisioningTests : IDisposable
{
    private const string Provisioning = "Provisioning";

    private const string CliSchemaName = AgentWorkspaceHostPushTests.SchemaName;

    private const string CliFolderName = "Contoso Helpdesk";

    private const string CloudOnlyTopic = CliSchemaName + ".topic.Greeting";

    private const string SettingsFile = "settings.mcs.yml";

    private const string MarkerFile = "agent.sync.yaml";

    private const string CloudCacheFile = ".mcs/botdefinition.json";

    private static readonly DateTime MarkerWrittenOn = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "provisioning-" + Guid.NewGuid().ToString("N"));

    private readonly HostedCloud _cloud = new();

    private readonly DirectoryPath _folder;

    private readonly DirectoryPath _cliFolder;

    private readonly WorkspaceSynchronizer _synchronizer;

    private readonly WorkspaceAttachService _attach;

    private readonly WorkspacePushService _push;

    public LocalModeProvisioningTests()
    {
        var files = new FileAccessorFactory();
        var root = new DirectoryPath(_root.Replace('\\', '/') + "/");
        var contexts = new OperationContextProvider();
        _folder = root.GetChildDirectoryPath("Agent D1");
        _cliFolder = root.GetChildDirectoryPath(CliFolderName);
        _synchronizer = new WorkspaceSynchronizer(new SyncMcsFileParser(LspProjectorService.Instance), files, _cloud.Island.Service.Object, new TestSyncProgress(new List<string>()), new LspComponentPathResolver());
        _attach = new WorkspaceAttachService(_synchronizer, _synchronizer, _synchronizer, contexts, files, _cloud.Clock);
        _push = new WorkspacePushService(_synchronizer, _synchronizer, contexts, files, _cloud.Clock);
        WorkspaceSnapshotFiles.Write(files, root, ClassicAgentFixtures.AgentD1("Agent D1"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task AttachAsync_OfAFolderToANewBotCopilotStudioIsStillProvisioning_WaitsBeforeWritingItsConnectorsWorkflowsAndPrompts()
    {
        _cloud.ReportProvisioningStatuses(Provisioning, Provisioning, "Provisioned");

        var attached = await _attach.AttachAsync(Request(), _cloud.Cloud.Client.Object, CancellationToken.None);

        var reads = _cloud.ProvisioningReads.ToArray();
        Assert.True(attached.AgentCreated);
        Assert.Equal(Enumerable.Repeat(attached.AgentId, 3), reads.Select(read => read.AgentId));
        Assert.Empty(_cloud.ContentWritesAmong(reads[2].DataverseCallsSoFar));
        Assert.Contains(nameof(ISyncDataverseClient.UpsertConnectorAsync), _cloud.ContentWritesAmong(int.MaxValue));
        Assert.Contains(nameof(ISyncDataverseClient.UpsertAIPromptAsync), _cloud.ContentWritesAmong(int.MaxValue));
        Assert.Equal(Seconds(2, 3), _cloud.Clock.Delays);
    }

    [Fact]
    public async Task AttachAsync_WhenTheNewBotIsStillProvisioningAtTheTimeout_FailsWithoutConnectingTheFolderAndTheNextAttachAdoptsTheBot()
    {
        _cloud.ProvisioningStatus = Provisioning;

        var failure = await Assert.ThrowsAsync<WorkspaceAttachException>(() => _attach.AttachAsync(Request(provisioningTimeoutSeconds: 10), _cloud.Cloud.Client.Object, CancellationToken.None));

        var botId = _cloud.AgentIdOf(ClassicAgentFixtures.AgentD1Schema);
        Assert.Equal((WorkspaceAttachFailure.ReattachFailed, (Guid?)botId), (failure.Failure, failure.AgentId));
        Assert.Equal(WorkspacePushErrorCodes.AgentNotProvisioned, Assert.IsType<WorkspacePushException>(failure.InnerException).Code);
        Assert.False(_synchronizer.IsSyncInfoAvailable(_folder));
        Assert.Empty(_cloud.ContentWritesAmong(int.MaxValue));
        Assert.Equal(Seconds(2, 3, 4, 1), _cloud.Clock.Delays);

        _cloud.ProvisioningStatus = "Provisioned";
        var attached = await _attach.AttachAsync(Request(), _cloud.Cloud.Client.Object, CancellationToken.None);

        Assert.Equal((botId, false, true), (attached.AgentId, attached.AgentCreated, attached.AdoptedExistingAgent));
        Assert.Single(_cloud.CreatedAgents);
    }

    [Fact]
    public async Task PushAsync_OfAFolderConnectedToABotThatIsProvisioningAgain_WaitsBeforeWritingAndThenPushes()
    {
        var attached = await _attach.AttachAsync(Request(), _cloud.Cloud.Client.Object, CancellationToken.None);
        var attachReads = _cloud.ProvisioningReads.Count;
        var attachWrites = _cloud.ContentWritesAmong(int.MaxValue).Count;
        _cloud.ReportProvisioningStatuses(Provisioning, "Provisioned");

        await _push.PushAsync(new WorkspacePushRequest { WorkspaceFolder = _folder, Overwrite = true }, _cloud.Cloud.Client.Object, CancellationToken.None);

        var pushReads = _cloud.ProvisioningReads.Skip(attachReads).ToArray();
        Assert.Equal(2, pushReads.Length);
        Assert.Equal(0, pushReads[1].SavesSoFar);
        Assert.Equal(attachWrites, _cloud.ContentWritesAmong(pushReads[1].DataverseCallsSoFar).Count);
        Assert.NotEmpty(_cloud.Island.ComponentsOf(attached.AgentId));
        Assert.Equal(Seconds(2), _cloud.Clock.Delays);
    }

    [Fact]
    public async Task PushAsync_WhenTheConnectedBotIsStillProvisioningAtTheTimeout_FailsWithAgentNotProvisionedBeforeWritingAnything()
    {
        var attached = await _attach.AttachAsync(Request(), _cloud.Cloud.Client.Object, CancellationToken.None);
        var attachWrites = _cloud.ContentWritesAmong(int.MaxValue).Count;
        _cloud.ProvisioningStatus = Provisioning;

        var failure = await Assert.ThrowsAsync<WorkspacePushException>(() => _push.PushAsync(new WorkspacePushRequest { WorkspaceFolder = _folder, Overwrite = true, ProvisioningTimeout = TimeSpan.FromSeconds(10) }, _cloud.Cloud.Client.Object, CancellationToken.None));

        Assert.Equal((WorkspacePushErrorCodes.AgentNotProvisioned, $"Copilot Studio was still provisioning the classic bot '{attached.AgentId}' after 00:00:10, so nothing was pushed into it. The bot is kept, and a later deploy pushes into the same bot once Copilot Studio finishes provisioning it."), (failure.Code, failure.Message));
        Assert.Empty(_cloud.Island.Saves);
        Assert.Equal(attachWrites, _cloud.ContentWritesAmong(int.MaxValue).Count);
        Assert.Equal(Seconds(2, 3, 4, 1), _cloud.Clock.Delays);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task AttachAsyncAndPushAsync_WithAProvisioningTimeoutThatIsNotPositive_AreRefusedBeforeAnyCloudCall(int timeoutSeconds)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _attach.AttachAsync(Request(timeoutSeconds), _cloud.Cloud.Client.Object, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _push.PushAsync(new WorkspacePushRequest { WorkspaceFolder = _folder, ProvisioningTimeout = TimeSpan.FromSeconds(timeoutSeconds) }, _cloud.Cloud.Client.Object, CancellationToken.None));

        Assert.Empty(_cloud.Cloud.Client.Invocations);
    }

    [Fact]
    public async Task AttachAsync_OfACliFolderToANewBotThatStaysProvisioningUntilItsFirstPublish_ConnectsTheFolderWithoutWaiting()
    {
        ScaffoldCliAgent();
        _cloud.ProvisioningStatus = Provisioning;

        var attached = await _attach.AttachAsync(Request(10, _cliFolder, CliSchemaName), _cloud.Cloud.Client.Object, CancellationToken.None);

        Assert.Equal((_cloud.AgentIdOf(CliSchemaName), true), (attached.AgentId, attached.AgentCreated));
        Assert.True(_synchronizer.IsSyncInfoAvailable(_cliFolder));
        Assert.Equal(attached.AgentId, Assert.Single(_cloud.ProvisioningReads).AgentId);
        Assert.Empty(_cloud.Clock.Delays);
    }

    [Fact]
    public async Task PushAsync_OfACliFolderConnectedToABotThatStaysProvisioningUntilItsFirstPublish_PushesWithoutWaiting()
    {
        ScaffoldCliAgent();
        await _attach.AttachAsync(Request(10, _cliFolder, CliSchemaName), _cloud.Cloud.Client.Object, CancellationToken.None);
        var attachSaves = _cloud.Island.Saves.Count;
        _cloud.ProvisioningStatus = Provisioning;

        Assert.NotEmpty((await _push.PushAsync(new WorkspacePushRequest { WorkspaceFolder = _cliFolder, Overwrite = true, ProvisioningTimeout = TimeSpan.FromSeconds(10) }, _cloud.Cloud.Client.Object, CancellationToken.None)).Changes);
        Assert.True(_cloud.Island.Saves.Count > attachSaves);
        Assert.Equal(2, _cloud.ProvisioningReads.Count);
        Assert.Empty(_cloud.Clock.Delays);
    }

    [Fact]
    public async Task PushAsync_OfAScaffoldedCliFolder_LeavesTheSettingsAndTheLayoutMarkerByteForByteAndUpdatesTheCloudCache()
    {
        ScaffoldCliAgent();
        var settings = File.ReadAllBytes(CliFile(SettingsFile));
        var marker = File.ReadAllBytes(CliFile(MarkerFile));

        await PushOverAChangeOnlyTheCloudMadeAsync();

        Assert.Equal(settings, File.ReadAllBytes(CliFile(SettingsFile)));
        Assert.Equal(marker, File.ReadAllBytes(CliFile(MarkerFile)));
        Assert.Contains(CloudOnlyTopic, File.ReadAllText(CliFile(CloudCacheFile)), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(SettingsMergeCanonicalizationTests.Formattings), MemberType = typeof(SettingsMergeCanonicalizationTests))]
    public async Task PushAsync_OfAScaffoldedCliFolderWhoseSettingsKeepTheirMeaningInAnotherFormat_LeavesThemByteForByte(string formatting)
    {
        ScaffoldCliAgent();
        File.WriteAllText(CliFile(SettingsFile), SettingsMergeCanonicalizationTests.Reformat(SettingsMergeCanonicalizationTests.AuthoredSettings.Replace("templ6_templ6", CliSchemaName, StringComparison.Ordinal), formatting));
        var settings = File.ReadAllBytes(CliFile(SettingsFile));

        await PushOverAChangeOnlyTheCloudMadeAsync();

        Assert.Equal(settings, File.ReadAllBytes(CliFile(SettingsFile)));
    }

    [Fact]
    public async Task PushAsync_OfAScaffoldedCliFolderWithoutItsLayoutMarker_WritesTheMarkerTheScaffoldWrote()
    {
        ScaffoldCliAgent();
        var scaffolded = File.ReadAllBytes(CliFile(MarkerFile));
        File.Delete(CliFile(MarkerFile));

        await PushOverAChangeOnlyTheCloudMadeAsync();

        Assert.Equal(scaffolded, File.ReadAllBytes(CliFile(MarkerFile)));
    }

    [Theory]
    [InlineData("\n", true)]
    [InlineData("\r\n", false)]
    [InlineData("\r\n", true)]
    public async Task PushAsync_OfAScaffoldedCliFolderWhoseLayoutMarkerDiffersOnlyInLineEndingsOrAByteOrderMark_LeavesTheMarkerUntouched(string lineEnding, bool byteOrderMark)
    {
        ScaffoldCliAgent();
        var checkedOut = ConnectionReferenceStoreTests.CheckedOut(File.ReadAllText(CliFile(MarkerFile)), lineEnding, byteOrderMark);
        File.WriteAllBytes(CliFile(MarkerFile), checkedOut);
        File.SetLastWriteTimeUtc(CliFile(MarkerFile), MarkerWrittenOn);

        await PushOverAChangeOnlyTheCloudMadeAsync();

        Assert.Equal(checkedOut, File.ReadAllBytes(CliFile(MarkerFile)));
        Assert.Equal(MarkerWrittenOn, File.GetLastWriteTimeUtc(CliFile(MarkerFile)));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task PushAsync_OfAScaffoldedCliFolderWhoseLayoutMarkerSaysSomethingElse_RewritesTheMarkerTheScaffoldWrote(string lineEnding)
    {
        ScaffoldCliAgent();
        var scaffolded = File.ReadAllBytes(CliFile(MarkerFile));
        File.WriteAllBytes(CliFile(MarkerFile), ConnectionReferenceStoreTests.CheckedOut("# Another layout\nlayoutVersion: 1\n", lineEnding, byteOrderMark: true));

        await PushOverAChangeOnlyTheCloudMadeAsync();

        Assert.Equal(scaffolded, File.ReadAllBytes(CliFile(MarkerFile)));
    }

    private async Task PushOverAChangeOnlyTheCloudMadeAsync()
    {
        var attached = await _attach.AttachAsync(Request(10, _cliFolder, CliSchemaName), _cloud.Cloud.Client.Object, CancellationToken.None);
        _cloud.Island.Seed(attached.AgentId, PushComponentCollectionExclusionTests.Topic(CloudOnlyTopic, "Provisioned In The Cloud"));
        _cloud.Island.SaveFailures.Enqueue(PushConcurrencyRetryTests.MakeConcurrencyException());

        await _push.PushAsync(new WorkspacePushRequest { WorkspaceFolder = _cliFolder, Overwrite = true }, _cloud.Cloud.Client.Object, CancellationToken.None);
    }

    private string CliFile(string relativePath) => Path.Combine(_root, CliFolderName, relativePath);

    private void ScaffoldCliAgent() => AgentScaffolder.Scaffold(new FileAccessorFactory().Create(_cliFolder), AgentAuthoringModes.CliCopilot, "Contoso Helpdesk", CliSchemaName, "Be helpful.");

    private WorkspaceAttachRequest Request(int provisioningTimeoutSeconds = 300, DirectoryPath? folder = null, string schemaName = ClassicAgentFixtures.AgentD1Schema) => new() { WorkspaceFolder = folder ?? _folder, DataverseEndpoint = HostedCloud.Organization, EnvironmentId = HostedCloud.EnvironmentId, TenantId = HostedCloud.TenantId, DisplayName = "Agent D1", SchemaName = schemaName, ProvisioningTimeout = TimeSpan.FromSeconds(provisioningTimeoutSeconds) };
}
