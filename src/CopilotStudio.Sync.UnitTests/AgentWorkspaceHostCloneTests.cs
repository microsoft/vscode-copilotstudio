// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Collections.Immutable;
using System.Net;
using Microsoft.Agents.Platform.Content;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Microsoft.CopilotStudio.Sync.Hosting;
using Moq;
using Xunit;
using static Microsoft.CopilotStudio.Sync.Dataverse.SyncDataverseClient;
using static Microsoft.CopilotStudio.Sync.UnitTests.AgentWorkspaceHostPushTests;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public sealed class AgentWorkspaceHostCloneTests : IDisposable
{
    private const string ToolsSchemaName = "cree9_tools";

    private const string ZetaSchemaName = "cree9_zeta";

    private static readonly DateTime CheckedOutOn = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly HostedCloud _cloud = new();

    private readonly RecordingFileAccessorFactory _files = new();

    private readonly RecordingAuthProvider _authentication = new();

    private readonly AgentWorkspaceHost _host;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "workspace-clone-" + Guid.NewGuid().ToString("N"));

    private readonly OperationContextProvider _contexts = new();

    private readonly WorkspaceSynchronizer _synchronizer;

    public AgentWorkspaceHostCloneTests()
    {
        _host = _cloud.CreateHost(_files);
        _synchronizer = new WorkspaceSynchronizer(new SyncMcsFileParser(LspProjectorService.Instance), new FileAccessorFactory(), _cloud.Island.Service.Object, new TestSyncProgress(new List<string>()), new LspComponentPathResolver());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task CloneAsync_WritesTheBytesTheCloneSequenceWritesAndReturnsThoseOutsideTheSyncState()
    {
        var botId = SeedHelpdesk();
        await CloneWithTheSequenceAsync(botId, "Contoso Helpdesk");

        var result = await _host.CloneAsync(new AgentCloneRequest(HostedCloud.Target(SchemaName), "Contoso Helpdesk", null!, null!), _authentication, CancellationToken.None);

        Assert.Equal(FilesOf(), new[] { "Contoso Helpdesk", "Shared Tools", "Zeta Tools" }.SelectMany(folder => _files.TakeFiles(folder).Select(file => (folder + "/" + file.Path, file.Content))).OrderBy(file => file.Item1, StringComparer.Ordinal).ToArray());
        Assert.Equal(FilesOf().Where(file => !SyncStatePaths.IsSyncStatePath(file.Path)).ToArray(), result.Snapshot.Files.Select(file => (file.Key, Convert.ToBase64String(file.Value.ToArray()))).OrderBy(file => file.Key, StringComparer.Ordinal));
        Assert.Equal(botId.ToString(), result.BotId);
    }

    [Theory]
    [InlineData("\r\n", false)]
    [InlineData("\n", true)]
    [InlineData("\r\n", true)]
    public async Task CloneAsync_OverItsOwnCloneCheckedOutWithOtherLineEndsOrAByteOrderMark_LeavesEveryTextFileOutsideTheSyncStateUntouched(string lineEnding, bool byteOrderMark)
    {
        var botId = SeedHelpdesk();
        _cloud.Cloud.AddConnectionReference("new_sharedmsnweather_08889", "/providers/Microsoft.PowerApps/apis/shared_msnweather");
        _cloud.AddWorkflow(botId, new WorkflowMetadata { WorkflowId = new Guid("6c1b0f9e-2d4a-4e8b-9f3c-5a7d1e2b4c68"), Name = "Weather", ClientData = Definition("shared_msnweather", "new_sharedmsnweather_08889"), StateCode = 1, StatusCode = 2 });
        _cloud.AddPrompt(botId, new AIPromptMetadata { AIModelId = new Guid("0d9e8f7a-6b5c-4d3e-8f1a-2b3c4d5e6f70"), Name = "Summarize", CustomConfiguration = "{\"version\":\"1\"}" });
        await CloneWithTheSequenceAsync(botId, "Contoso Helpdesk");
        await CloneWithTheSequenceAsync(botId, "Contoso Helpdesk");
        foreach (var file in TextFiles())
        {
            File.WriteAllBytes(file, ConnectionReferenceStoreTests.CheckedOut(File.ReadAllText(file), lineEnding, byteOrderMark));
            File.SetLastWriteTimeUtc(file, CheckedOutOn);
        }

        var checkedOut = TextFilesWithTheirTimestamps();

        await CloneWithTheSequenceAsync(botId, "Contoso Helpdesk");

        Assert.Equal(checkedOut, TextFilesWithTheirTimestamps());
        Assert.Equal(
            new[]
            {
                "Contoso Helpdesk/agent.mcs.yml",
                "Contoso Helpdesk/connectionreferences.mcs.yml",
                "Contoso Helpdesk/prompts/Summarize-0d9e8f7a-6b5c-4d3e-8f1a-2b3c4d5e6f70/metadata.yml",
                "Contoso Helpdesk/prompts/Summarize-0d9e8f7a-6b5c-4d3e-8f1a-2b3c4d5e6f70/prompt.json",
                "Contoso Helpdesk/references.mcs.yml",
                "Contoso Helpdesk/settings.mcs.yml",
                "Contoso Helpdesk/topics/Greeting.mcs.yml",
                "Contoso Helpdesk/workflows/Weather-6c1b0f9e-2d4a-4e8b-9f3c-5a7d1e2b4c68/metadata.yml",
                "Contoso Helpdesk/workflows/Weather-6c1b0f9e-2d4a-4e8b-9f3c-5a7d1e2b4c68/workflow.json",
                "Shared Tools/collection.mcs.yml",
                "Shared Tools/topics/Lookup.mcs.yml",
                "Zeta Tools/collection.mcs.yml",
                "Zeta Tools/topics/Escalate.mcs.yml",
            },
            checkedOut.Select(file => file.Path));
    }

    [Fact]
    public async Task CloneAsync_WhenTheCloneFailsAfterWriting_ThrowsCloneFailedWithTheCauseAndReleasesWhatItWrote()
    {
        SeedHelpdesk();
        _cloud.Island.Pauses = FailOnCollections;

        var failure = await Assert.ThrowsAsync<AgentWorkspaceHostException>(() => CloneAsync(HostedCloud.Target(SchemaName), new Dictionary<string, string>(), Array.Empty<string>()));

        Assert.Equal(AgentWorkspaceHostErrorCodes.CloneFailed, failure.Code);
        Assert.Equal("The collection cannot be read.", Assert.IsType<InvalidOperationException>(failure.InnerException).Message);
        Assert.Contains("topics/Greeting.mcs.yml", _files.TakeFiles("Agent").Select(file => file.Path));
        Assert.Contains(".mcs/conn.json", _files.TakeFiles("Shared Tools").Select(file => file.Path));
        Assert.Equal(0, _files.LiveCount);
    }

    [Fact]
    public async Task CloneAllAssetsAsync_WritesTheBytesTheCloneSequenceWritesWithEachFolderItsOwnSyncInfo()
    {
        var botId = SeedHelpdesk();
        await CloneWithTheSequenceAsync(botId, "Contoso Helpdesk");
        var expected = FilesOf();
        Directory.Delete(_root, recursive: true);

        var folders = await CloneAllAssetsAsync(botId);

        Assert.Equal(expected, FilesOf());
        Assert.Equal(new[] { "Contoso Helpdesk", "Shared Tools", "Zeta Tools" }.Select(Root().GetChildDirectoryPath), folders);
    }

    [Fact]
    public async Task CloneAllAssetsAsync_WhenAFolderIsNotEmpty_RefusesBeforeCloningAnything()
    {
        var botId = SeedHelpdesk();
        Directory.CreateDirectory(Path.Combine(_root, "Zeta Tools"));
        File.WriteAllText(Path.Combine(_root, "Zeta Tools", "notes.txt"), "mine");

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => CloneAllAssetsAsync(botId));

        Assert.Equal($"Destination path '{Root().GetChildDirectoryPath("Zeta Tools")}' already exists and is not an empty directory.", failure.Message);
        Assert.Equal(new[] { "Zeta Tools/notes.txt" }, FilesOf().Select(file => file.Path));
    }

    [Fact]
    public async Task CloneAllAssetsAsync_WhenTwoAssetsTakeOneFolder_RefusesBeforeCloningAnything()
    {
        var botId = SeedHelpdesk();
        _cloud.Link(botId, _cloud.SeedCollection("cree9_same", "Contoso Helpdesk", PushComponentCollectionExclusionTests.Topic("cree9_same.topic.Echo", "Echo")));

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => CloneAllAssetsAsync(botId));

        Assert.Equal($"Destination path '{Root().GetChildDirectoryPath("Contoso Helpdesk")}' already exists and is not an empty directory.", failure.Message);
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task CloneAsync_WritesTheLinkedCollectionsIntoFreeFoldersAndPointsTheReferencesAtThem()
    {
        var botId = _cloud.SeedAgent(SchemaName, "Contoso Helpdesk", PushComponentCollectionExclusionTests.Topic($"{SchemaName}.topic.Greeting", "Greeting"));
        _cloud.Link(botId, _cloud.SeedCollection("cree9_tools", "Shared Tools", PushComponentCollectionExclusionTests.Topic("cree9_tools.topic.Lookup", "Lookup")));
        _cloud.Link(botId, _cloud.SeedCollection("cree9_zeta", "Zeta Tools", PushComponentCollectionExclusionTests.Topic("cree9_zeta.topic.Escalate", "Escalate")));

        var result = await CloneAsync(HostedCloud.Target(SchemaName), new Dictionary<string, string> { ["CREE9_ZETA"] = "Zeta" }, new[] { "shared tools" });
        var validation = await _host.ValidateAsync(result.Snapshot, CancellationToken.None);

        Assert.Equal(botId.ToString(), result.BotId);
        Assert.Contains("Shared Tools-2/collection.mcs.yml", result.Snapshot.Files.Keys);
        Assert.Contains("Zeta/topics/Escalate.mcs.yml", result.Snapshot.Files.Keys);
        Assert.Contains("directory: ../Shared Tools-2/", Text(result.Snapshot, "Agent/references.mcs.yml"), StringComparison.Ordinal);
        Assert.Contains("directory: ../Zeta/", Text(result.Snapshot, "Agent/references.mcs.yml"), StringComparison.Ordinal);
        Assert.True(validation.IsValid, string.Join("; ", validation.Diagnostics.Select(diagnostic => diagnostic.Message)));
        Assert.Equal(AgentAuthoringModes.Classic, validation.AuthoringMode);
        Assert.Equal(new[] { "Shared Tools-2", "Zeta" }, validation.DeclaredCollectionFolders.OrderBy(folder => folder, StringComparer.Ordinal));
        Assert.DoesNotContain(result.Snapshot.Files.Keys, SyncStatePaths.IsSyncStatePath);
        Assert.Equal(0, _files.LiveCount);
    }

    [Fact]
    public async Task TheCloneSequence_ClonesEveryAssetUnderItsOwnSyncInfoBeforeApplyingTheTouchupsOfAny()
    {
        var client = _cloud.Cloud.Client.Object;
        var synchronizer = new Mock<IWorkspaceSynchronizer>(MockBehavior.Strict);
        var calls = new List<(string Call, DirectoryPath Folder, Guid? CollectionId)>();
        var trackers = new HashSet<ReferenceTracker>();
        var collectionId = Guid.NewGuid();
        var agentFolder = Root().GetChildDirectoryPath("Contoso Helpdesk");
        var toolsFolder = Root().GetChildDirectoryPath("Shared Tools");
        var syncInfo = await WorkspaceCloneSequence.ForAgentAsync(client, Guid.NewGuid(), HostedCloud.Organization, HostedCloud.EnvironmentId, HostedCloud.TenantId, CancellationToken.None);
        synchronizer.Setup(workspaces => workspaces.SaveSyncInfoAsync(It.IsAny<DirectoryPath>(), It.IsAny<AgentSyncInfo>())).Callback<DirectoryPath, AgentSyncInfo>((folder, saved) => calls.Add((nameof(IWorkspaceSynchronizer.SaveSyncInfoAsync), folder, saved.ComponentCollectionId))).Returns(Task.CompletedTask);
        synchronizer.Setup(workspaces => workspaces.CloneChangesAsync(It.IsAny<DirectoryPath>(), It.IsAny<ReferenceTracker>(), It.IsAny<AuthoringOperationContextBase>(), client, It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>())).Callback<DirectoryPath, ReferenceTracker, AuthoringOperationContextBase, ISyncDataverseClient, AgentSyncInfo, CancellationToken>((folder, tracker, _, _, cloned, _) =>
        {
            calls.Add((nameof(IWorkspaceSynchronizer.CloneChangesAsync), folder, cloned.ComponentCollectionId));
            trackers.Add(tracker);
        }).Returns(Task.CompletedTask);
        synchronizer.Setup(workspaces => workspaces.ApplyTouchupsAsync(It.IsAny<DirectoryPath>(), It.IsAny<ReferenceTracker>(), It.IsAny<CancellationToken>())).Callback<DirectoryPath, ReferenceTracker, CancellationToken>((folder, tracker, _) =>
        {
            calls.Add((nameof(IWorkspaceSynchronizer.ApplyTouchupsAsync), folder, null));
            trackers.Add(tracker);
        }).Returns(Task.CompletedTask);

        await WorkspaceCloneSequence.CloneAsync(synchronizer.Object, new[] { new KeyValuePair<AuthoringOperationContextBase, DirectoryPath>(await _contexts.GetAsync(syncInfo), agentFolder), new KeyValuePair<AuthoringOperationContextBase, DirectoryPath>(await _contexts.GetAsync(WorkspaceCloneSequence.ForComponentCollection(syncInfo, collectionId)), toolsFolder) }, syncInfo, client, CancellationToken.None);

        Assert.Equal(new (string, DirectoryPath, Guid?)[] { (nameof(IWorkspaceSynchronizer.SaveSyncInfoAsync), agentFolder, null), (nameof(IWorkspaceSynchronizer.CloneChangesAsync), agentFolder, null), (nameof(IWorkspaceSynchronizer.SaveSyncInfoAsync), toolsFolder, collectionId), (nameof(IWorkspaceSynchronizer.CloneChangesAsync), toolsFolder, collectionId), (nameof(IWorkspaceSynchronizer.ApplyTouchupsAsync), agentFolder, null), (nameof(IWorkspaceSynchronizer.ApplyTouchupsAsync), toolsFolder, null) }, calls);
        Assert.Single(trackers);
    }

    [Theory]
    [InlineData(null, ToolsSchemaName, ToolsSchemaName)]
    [InlineData(" ", ToolsSchemaName, ToolsSchemaName)]
    [InlineData(null, null, null)]
    [InlineData("", " ", null)]
    public async Task CloneAsync_OfACollectionTheEnvironmentReportsWithoutADisplayName_NamesItsFolderAfterItsSchemaNameOrElseItsId(string? displayName, string? schemaName, string? folder)
    {
        var botId = _cloud.SeedAgent(SchemaName, "Contoso Helpdesk", PushComponentCollectionExclusionTests.Topic($"{SchemaName}.topic.Greeting", "Greeting"));
        var toolsId = _cloud.SeedCollection(ToolsSchemaName, "Shared Tools", PushComponentCollectionExclusionTests.Topic($"{ToolsSchemaName}.topic.Lookup", "Lookup"));
        _cloud.Link(botId, toolsId);
        _cloud.ReportCollectionAs(toolsId, displayName, schemaName);

        var result = await CloneAsync(HostedCloud.Target(SchemaName), new Dictionary<string, string> { [ZetaSchemaName] = "Zeta" }, Array.Empty<string>());

        Assert.Contains($"{folder ?? toolsId.ToString()}/collection.mcs.yml", result.Snapshot.Files.Keys);
    }

    [Fact]
    public async Task CloneAsync_ThenPushAsync_LeavesTheBotAndItsCollectionsAsTheyWere()
    {
        var botId = _cloud.SeedAgent(SchemaName, "Contoso Helpdesk", PushComponentCollectionExclusionTests.Topic($"{SchemaName}.topic.Greeting", "Greeting"));
        var toolsId = _cloud.SeedCollection("cree9_tools", "Shared Tools", PushComponentCollectionExclusionTests.Topic("cree9_tools.topic.Lookup", "Lookup"));
        _cloud.Link(botId, toolsId);
        var before = new[] { Fingerprint(botId), Fingerprint(toolsId) };

        var clone = await CloneAsync(HostedCloud.Target(SchemaName, botId.ToString()), new Dictionary<string, string>(), Array.Empty<string>());
        var result = await _host.PushAsync(clone.Snapshot, HostedCloud.Target(SchemaName, clone.BotId), HostedCloud.Options(createIfMissing: false), _authentication, CancellationToken.None);

        Assert.Equal(clone.BotId, result.BotId);
        Assert.True(result.Published);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(before, new[] { Fingerprint(botId), Fingerprint(toolsId) });
        Assert.Equal(new[] { SchemaName }, _cloud.CreatedAgents);
        Assert.Equal(new[] { botId }, await _cloud.Collections.Object.GetAgentIdsForComponentCollectionAsync(toolsId, CancellationToken.None));
    }

    [Fact]
    public async Task CloneAsync_OfABotTheEnvironmentDoesNotHold_ThrowsBotNotFoundBeforeWritingAnything()
    {
        var missingId = Guid.NewGuid();

        var bySchemaName = await Assert.ThrowsAsync<AgentWorkspaceHostException>(() => CloneAsync(HostedCloud.Target(SchemaName), new Dictionary<string, string>(), Array.Empty<string>()));
        var byIdentifier = await Assert.ThrowsAsync<AgentWorkspaceHostException>(() => CloneAsync(HostedCloud.Target(SchemaName, missingId.ToString()), new Dictionary<string, string>(), Array.Empty<string>()));

        Assert.Equal(AgentWorkspaceHostErrorCodes.BotNotFound, bySchemaName.Code);
        Assert.Contains($"'{SchemaName}'", bySchemaName.Message, StringComparison.Ordinal);
        Assert.Null(bySchemaName.InnerException);
        Assert.Equal(AgentWorkspaceHostErrorCodes.BotNotFound, byIdentifier.Code);
        Assert.Contains($"'{missingId}'", byIdentifier.Message, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, Assert.IsType<DataverseRequestException>(byIdentifier.InnerException).StatusCode);
        Assert.DoesNotContain(_files.Opened, root => root.EndsWith("/Agent/", StringComparison.Ordinal));
        Assert.Equal(0, _files.LiveCount);
    }

    [Theory]
    [InlineData("Agent/Nested", "Tools")]
    [InlineData("..", "Tools")]
    [InlineData(".MCS", "Tools")]
    [InlineData(" ", "Tools")]
    [InlineData("Agent", "agent")]
    [InlineData("Agent", "Tools\\Nested")]
    [InlineData("CON", "Tools")]
    [InlineData("Agent", "lpt1.tools")]
    [InlineData("Agent", "Tools ")]
    public async Task CloneAsync_WithFoldersThatAreNotDistinctTopLevelFolders_ThrowsBeforeAnyCloudCall(string folder, string collectionFolder)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _host.CloneAsync(new AgentCloneRequest(HostedCloud.Target(SchemaName), folder, new Dictionary<string, string> { ["cree9_tools"] = collectionFolder }, Array.Empty<string>()), _authentication, CancellationToken.None));

        Assert.Empty(_cloud.Cloud.Client.Invocations);
        Assert.Empty(_files.Opened);
    }

    [Fact]
    public async Task CloneAsync_WithABotIdThatIsNotAnIdentifier_ThrowsBeforeAnyCloudCall()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => CloneAsync(HostedCloud.Target(SchemaName, "not-an-identifier"), new Dictionary<string, string>(), Array.Empty<string>()));

        Assert.Empty(_cloud.Cloud.Client.Invocations);
    }

    [Fact]
    public async Task CloneAsync_WithTheCollectionsAValidationDeclares_WritesEachCollectionIntoItsDeclaredFolder()
    {
        var botId = _cloud.SeedAgent(SchemaName, "Contoso Helpdesk");
        _cloud.Link(botId, _cloud.SeedCollection("cree9_tools", "Lookup Tools", PushComponentCollectionExclusionTests.Topic("cree9_tools.topic.Lookup", "Lookup")));
        _cloud.Link(botId, _cloud.SeedCollection("cree9_zeta", "Escalation Tools", PushComponentCollectionExclusionTests.Topic("cree9_zeta.topic.Escalate", "Escalate")));

        var result = await _host.CloneAsync(new AgentCloneRequest(HostedCloud.Target(SchemaName), "Agent", (await _host.ValidateAsync(ScaffoldWithCollections(), CancellationToken.None)).DeclaredCollections, Array.Empty<string>()), _authentication, CancellationToken.None);

        Assert.Contains("Shared Tools/topics/Lookup.mcs.yml", result.Snapshot.Files.Keys);
        Assert.Contains("Zeta Tools/topics/Escalate.mcs.yml", result.Snapshot.Files.Keys);
        Assert.DoesNotContain(result.Snapshot.Files.Keys, path => path.StartsWith("Lookup Tools/", StringComparison.Ordinal) || path.StartsWith("Escalation Tools/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CloneAsync_WithAFolderCheck_SkipsTheNamesItReportsTakenAndAsksOnlyAboutNamesOutsideTheKnownOccupiedAndChosenFolders()
    {
        var botId = _cloud.SeedAgent(SchemaName, "Contoso Helpdesk");
        _cloud.Link(botId, _cloud.SeedCollection("cree9_tools", "Shared Tools", PushComponentCollectionExclusionTests.Topic("cree9_tools.topic.Lookup", "Lookup")));
        _cloud.Link(botId, _cloud.SeedCollection("cree9_zeta", "Zeta Tools", PushComponentCollectionExclusionTests.Topic("cree9_zeta.topic.Escalate", "Escalate")));
        _cloud.Link(botId, _cloud.SeedCollection("cree9_more", "Zeta Tools", PushComponentCollectionExclusionTests.Topic("cree9_more.topic.Route", "Route")));
        _cloud.Link(botId, _cloud.SeedCollection("cree9_alpha", "Alpha", PushComponentCollectionExclusionTests.Topic("cree9_alpha.topic.Start", "Start")));
        var asked = new List<string>();
        var takenInRepository = new HashSet<string>(new[] { "Shared Tools-2", "zeta tools" }, StringComparer.OrdinalIgnoreCase);

        var result = await _host.CloneAsync(new AgentCloneRequest(HostedCloud.Target(SchemaName), "Agent", new Dictionary<string, string> { ["cree9_alpha"] = "Alpha" }, new[] { "shared tools" }) { IsFolderTaken = (folder, _) => { asked.Add(folder); return new ValueTask<bool>(takenInRepository.Contains(folder)); } }, _authentication, CancellationToken.None);

        Assert.Contains("Shared Tools-3/topics/Lookup.mcs.yml", result.Snapshot.Files.Keys);
        Assert.Contains("Zeta Tools-2/topics/Escalate.mcs.yml", result.Snapshot.Files.Keys);
        Assert.Equal(new[] { "Shared Tools-2", "Shared Tools-3", "Zeta Tools", "Zeta Tools-2", "Zeta Tools-3" }, asked.OrderBy(folder => folder, StringComparer.Ordinal));
        Assert.Contains("Zeta Tools-3/topics/Route.mcs.yml", result.Snapshot.Files.Keys);
        Assert.Contains("Alpha/topics/Start.mcs.yml", result.Snapshot.Files.Keys);
        Assert.Equal(0, _files.LiveCount);
    }

    [Fact]
    public async Task CloneAsync_WhenCancelledDuringTheFolderCheck_RethrowsTheCancellationAndReleasesTheSession()
    {
        _cloud.Link(_cloud.SeedAgent(SchemaName, "Contoso Helpdesk"), _cloud.SeedCollection("cree9_tools", "Shared Tools"));
        using var cancellation = new CancellationTokenSource();
        var received = CancellationToken.None;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _host.CloneAsync(new AgentCloneRequest(HostedCloud.Target(SchemaName), "Agent", new Dictionary<string, string>(), Array.Empty<string>()) { IsFolderTaken = (_, token) => { received = token; cancellation.Cancel(); token.ThrowIfCancellationRequested(); return new ValueTask<bool>(false); } }, _authentication, cancellation.Token));

        Assert.Equal(cancellation.Token, received);
        Assert.Equal(0, _files.LiveCount);
    }

    [Fact]
    public async Task CloneAsync_WhenTheFolderCheckFails_ThrowsCloneFailedWithTheCause()
    {
        _cloud.Link(_cloud.SeedAgent(SchemaName, "Contoso Helpdesk"), _cloud.SeedCollection("cree9_tools", "Shared Tools"));
        var unlisted = new InvalidOperationException("The repository's folders could not be listed.");

        var failure = await Assert.ThrowsAsync<AgentWorkspaceHostException>(() => _host.CloneAsync(new AgentCloneRequest(HostedCloud.Target(SchemaName), "Agent", new Dictionary<string, string>(), Array.Empty<string>()) { IsFolderTaken = (_, _) => throw unlisted }, _authentication, CancellationToken.None));

        Assert.Equal(AgentWorkspaceHostErrorCodes.CloneFailed, failure.Code);
        Assert.Same(unlisted, failure.InnerException);
        Assert.Equal(0, _files.LiveCount);
    }

    [Theory]
    [InlineData("Contoso Helpdesk", SchemaName, "Be helpful.")]
    [InlineData(AgentScaffolderTests.EscapedDisplayName, AgentScaffolderTests.EscapedSchemaName, AgentScaffolderTests.EscapedInstructions)]
    public async Task CloneAsync_OfACliScaffoldWhosePushCreatedTheBot_WritesEveryFileTheScaffoldWroteByteForByte(string displayName, string schemaName, string instructions)
    {
        var scaffold = Scaffolds.Agent("Agent", displayName, schemaName, AgentAuthoringModes.CliCopilot, instructions: instructions);
        var pushed = await _host.PushAsync(scaffold, HostedCloud.Target(schemaName), HostedCloud.Options(), _authentication, CancellationToken.None);

        AssertSameFiles(scaffold, (await CloneAsync(HostedCloud.Target(schemaName, pushed.BotId), new Dictionary<string, string>(), Array.Empty<string>())).Snapshot);
    }

    [Theory]
    [InlineData("Contoso Helpdesk", SchemaName, "Be helpful.")]
    [InlineData(AgentScaffolderTests.EscapedDisplayName, AgentScaffolderTests.EscapedSchemaName, AgentScaffolderTests.EscapedInstructions)]
    public async Task CloneAsync_OfAClassicScaffoldPushedToABotOfItsTemplate_WritesEveryFileTheScaffoldWroteByteForByte(string displayName, string schemaName, string instructions)
    {
        _cloud.Island.Normalizes = WorkspaceSynchronizer.RoundTripThroughServiceJson;
        var scaffold = Scaffolds.Agent("Agent", displayName, schemaName, AgentAuthoringModes.Classic, instructions: instructions);
        var botId = _cloud.SeedAgent(schemaName, "Contoso Helpdesk");
        var bot = _cloud.Island.EntityOf(botId)!.ToBuilder();
        bot.Template = CliAgentBotEntityReader.Parse(Text(scaffold, "Agent/settings.mcs.yml")).Template;
        _cloud.Island.SetEntity(botId, bot.Build());

        await _host.PushAsync(scaffold, HostedCloud.Target(schemaName, botId.ToString()), HostedCloud.Options(), _authentication, CancellationToken.None);

        AssertSameFiles(scaffold, (await CloneAsync(HostedCloud.Target(schemaName, botId.ToString()), new Dictionary<string, string>(), Array.Empty<string>())).Snapshot);
    }

    internal static void AssertSameFiles(AgentWorkspaceSnapshot expected, AgentWorkspaceSnapshot actual)
    {
        Assert.Equal(expected.Files.Keys.OrderBy(path => path, StringComparer.Ordinal), actual.Files.Keys.OrderBy(path => path, StringComparer.Ordinal));
        Assert.All(expected.Files, file => Assert.Equal(file.Value.ToArray(), actual.Files[file.Key].ToArray()));
    }

    private (string SchemaName, Guid Id, long Version)[] Fingerprint(Guid identity) => _cloud.Island.ComponentsOf(identity).Select(component => (component.SchemaNameString, component.Id.Value, component.Version)).OrderBy(component => component.SchemaNameString, StringComparer.Ordinal).ToArray();

    private Task<AgentCloneResult> CloneAsync(AgentSyncTarget source, IReadOnlyDictionary<string, string> collectionFolders, IReadOnlyCollection<string> occupiedFolders) => _host.CloneAsync(new AgentCloneRequest(source, "Agent", collectionFolders, occupiedFolders), _authentication, CancellationToken.None);

    private static Task FailOnCollections(AuthoringOperationContextBase context) => context is BotComponentCollectionAuthoringOperationContext ? Task.FromException(new InvalidOperationException("The collection cannot be read.")) : Task.CompletedTask;

    private Guid SeedHelpdesk()
    {
        var botId = _cloud.SeedAgent(SchemaName, "Contoso Helpdesk", PushComponentCollectionExclusionTests.Topic($"{SchemaName}.topic.Greeting", "Greeting"));
        _cloud.Link(botId, _cloud.SeedCollection(ToolsSchemaName, "Shared Tools", PushComponentCollectionExclusionTests.Topic($"{ToolsSchemaName}.topic.Lookup", "Lookup")));
        _cloud.Link(botId, _cloud.SeedCollection(ZetaSchemaName, "Zeta Tools", PushComponentCollectionExclusionTests.Topic($"{ZetaSchemaName}.topic.Escalate", "Escalate")));

        return botId;
    }

    private DirectoryPath Root() => new(_root.Replace('\\', '/') + "/");

    private async Task<ImmutableArray<DirectoryPath>> CloneAllAssetsAsync(Guid botId)
    {
        var client = _cloud.Cloud.Client.Object;
        var agent = await client.GetAgentInfoAsync(botId, CancellationToken.None);
        var syncInfo = await WorkspaceCloneSequence.ForAgentAsync(client, botId, HostedCloud.Organization, HostedCloud.EnvironmentId, HostedCloud.TenantId, CancellationToken.None);

        return await _synchronizer.CloneAllAssetsAsync(Root(), syncInfo, new AssetsToClone { CloneAgent = true, ComponentCollectionIds = agent.ComponentCollections.Select(collection => collection.Id).ToImmutableArray() }, agent, _contexts, client, CancellationToken.None);
    }

    private async Task CloneWithTheSequenceAsync(Guid botId, string agentFolder)
    {
        var client = _cloud.Cloud.Client.Object;
        var agent = await client.GetAgentInfoAsync(botId, CancellationToken.None);
        var syncInfo = await WorkspaceCloneSequence.ForAgentAsync(client, botId, HostedCloud.Organization, HostedCloud.EnvironmentId, HostedCloud.TenantId, CancellationToken.None);
        var contexts = await _contexts.GetAllAsync(syncInfo, new AssetsToClone { CloneAgent = true, ComponentCollectionIds = agent.ComponentCollections.Select(collection => collection.Id).ToImmutableArray() });
        var root = Root();

        await WorkspaceCloneSequence.CloneAsync(_synchronizer, contexts.Select(context => new KeyValuePair<AuthoringOperationContextBase, DirectoryPath>(context, root.GetChildDirectoryPath(context is BotComponentCollectionAuthoringOperationContext collection ? WorkspaceFolderNames.ForProject(agent.ComponentCollections.Single(linked => linked.Id == collection.BotComponentCollectionReference.CdsId).DisplayName!) : agentFolder))).ToArray(), syncInfo, client, CancellationToken.None);
    }

    private (string Path, string Content)[] FilesOf() => Directory.Exists(_root) ? Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Select(file => (Path.GetRelativePath(_root, file).Replace('\\', '/'), Convert.ToBase64String(File.ReadAllBytes(file)))).OrderBy(file => file.Item1, StringComparer.Ordinal).ToArray() : Array.Empty<(string, string)>();

    private IEnumerable<string> TextFiles() => Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Where(file => !SyncStatePaths.IsSyncStatePath(Path.GetRelativePath(_root, file).Replace('\\', '/')) && !file.EndsWith(".png", StringComparison.OrdinalIgnoreCase));

    private (string Path, string Content, DateTime WrittenOn)[] TextFilesWithTheirTimestamps() => TextFiles().Select(file => (Path.GetRelativePath(_root, file).Replace('\\', '/'), Convert.ToBase64String(File.ReadAllBytes(file)), File.GetLastWriteTimeUtc(file))).OrderBy(file => file.Item1, StringComparer.Ordinal).ToArray();
}
