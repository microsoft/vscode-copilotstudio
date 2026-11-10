// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Text;
using System.Text.Json;
using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.ObjectModel.Yaml;
using Microsoft.Agents.Platform.Content;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Microsoft.CopilotStudio.Sync.Hosting;
using Moq;
using Xunit;
using static Microsoft.CopilotStudio.Sync.UnitTests.AgentWorkspaceHostPushTests;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public sealed class AuthoredSettingsPushTests : IDisposable
{
    internal const string AuthoredPublishedOn = "publishedOn: 2026-09-10T15:58:14.0000000Z\n";

    private const string Folder = "Agent D1";

    private const string SettingsFile = Folder + "/settings.mcs.yml";

    internal static readonly DateTime BotPublishedOn = new(2026, 10, 20, 8, 0, 0, DateTimeKind.Utc);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "authored-settings-" + Guid.NewGuid().ToString("N"));

    private readonly HostedCloud _cloud = new();

    private readonly RecordingFileAccessorFactory _files = new();

    private readonly RecordingAuthProvider _authentication = new();

    public static TheoryData<AuthoringShape, string> CopiesRenamedOnDisk => new()
    {
        { AuthoringShape.Classic, "Contoso Copy" },
        { AuthoringShape.CliCopilot, "Contoso Copy" },
        { AuthoringShape.Classic, new string('C', 42) },
        { AuthoringShape.CliCopilot, new string('C', 42) },
    };

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PushAsync_ToABotTheDeployCreatesOrAdopts_SavesTheBotWithEveryAuthoredSettingAndKeepsItsTemplateAndPublishState(bool adopted)
    {
        var seeded = adopted ? Published(_cloud, _cloud.SeedAgent(ClassicAgentFixtures.AgentD1Schema, "Contoso Helpdesk")) : (Guid?)null;
        var snapshot = AgentD1();

        var result = await PushAsync(snapshot);

        var botId = Guid.Parse(result.BotId);
        var saved = Assert.Single(_cloud.Island.SavedChanges, changes => changes.Bot is not null).Bot!;
        Assert.Equal(seeded ?? _cloud.AgentIdOf(ClassicAgentFixtures.AgentD1Schema), botId);
        Assert.Single(_cloud.CreatedAgents);
        Assert.True(result.Published);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(Expected(Text(snapshot, SettingsFile), "empty-1.0.0"), SettingsOf(saved));
        Assert.Equal(SettingsOf(saved), SettingsOf(_cloud.Island.EntityOf(botId)!));
        Assert.Equal(adopted ? BotPublishedOn : null, _cloud.Island.EntityOf(botId)!.PublishedOn);
    }

    [Fact]
    public async Task PushAsync_ToABotWhoseSettingsDriftedInThePortal_RestoresTheAuthoredSettingsAndKeepsItsPublishState()
    {
        var snapshot = AgentD1();
        var botId = Guid.Parse((await PushAsync(snapshot)).BotId);
        _cloud.Island.SetEntity(botId, CodeSerializer.Deserialize<BotEntity>($"schemaName: {ClassicAgentFixtures.AgentD1Schema}\ndisplayName: Renamed In The Portal\nauthenticationMode: None\nauthenticationTrigger: AsNeeded\nconfiguration:\n  recognizer:\n    kind: GenerativeAIRecognizer\ntemplate: empty-1.0.0\nlanguage: 1033\npublishedOn: 2026-10-20T08:00:00.0000000Z\n")!);
        var saves = _cloud.Island.SavedChanges.Count;

        var redeployed = await PushAsync(snapshot, botId.ToString());

        Assert.Equal((botId.ToString(), true), (redeployed.BotId, redeployed.Published));
        Assert.Single(_cloud.Island.SavedChanges.Skip(saves), changes => changes.Bot is not null);
        Assert.Equal(Expected(Text(snapshot, SettingsFile), "empty-1.0.0"), SettingsOf(_cloud.Island.EntityOf(botId)!));
        Assert.Equal(BotPublishedOn, _cloud.Island.EntityOf(botId)!.PublishedOn);
    }

    [Fact]
    public async Task PushAsync_WhenTheBotAlreadyCarriesTheAuthoredSettings_SavesTheComponentChangesWithoutTouchingTheBot()
    {
        var snapshot = AgentD1();
        var created = await PushAsync(snapshot);
        var botId = Guid.Parse(created.BotId);
        var entity = _cloud.Island.EntityOf(botId);
        var saves = _cloud.Island.SavedChanges.Count;

        await PushAsync(With(snapshot, $"{Folder}/topics/Greeting.mcs.yml", ClassicAgentFixtures.Topic("Greeting", "Welcome back.")), created.BotId);

        Assert.NotEmpty(_cloud.Island.SavedChanges.Skip(saves));
        Assert.All(_cloud.Island.SavedChanges.Skip(saves), changes => Assert.Null(changes.Bot));
        Assert.Same(entity, _cloud.Island.EntityOf(botId));
    }

    [Fact]
    public async Task PushAsync_OfACliAgent_SavesTheBotWithItsAuthoredSettingsAndKeepsTheCliTemplate()
    {
        var host = _cloud.CreateHost(_files);
        var scaffolded = Scaffolds.Agent("Agent", "Contoso Helpdesk", SchemaName, AgentAuthoringModes.CliCopilot, instructions: "Be helpful.");
        var authored = WithAuthoredPublishedOn(Text(scaffolded, "Agent/settings.mcs.yml").Replace("template: cliagent-1.0.0", "template: default-2.1.0", StringComparison.Ordinal));

        var entity = _cloud.Island.EntityOf(Guid.Parse((await host.PushAsync(With(scaffolded, "Agent/settings.mcs.yml", authored), HostedCloud.Target(SchemaName), HostedCloud.Options(), _authentication, CancellationToken.None)).BotId))!;
        Assert.Equal(SettingsOf(entity), SettingsOf(Assert.Single(_cloud.Island.SavedChanges, changes => changes.Bot is not null).Bot!));
        Assert.Equal(Expected(authored, "cliagent-1.0.0"), SettingsOf(entity));
        Assert.Null(entity.PublishedOn);
        Assert.Equal(AuthoringShape.CliCopilot, AgentClassifier.DetectAuthoringShape(entity));
    }

    [Fact]
    public async Task PushAsync_WhenTheServiceStoresOtherSettingsThanItWasSent_LeavesTheCacheEqualToAFreshClone()
    {
        _cloud.Island.NormalizesBot = bot =>
        {
            var builder = bot.ToBuilder();
            builder.DisplayName = "Named By The Service";

            return builder.Build();
        };
        var host = _cloud.CreateHost(_files);

        var result = await host.PushAsync(Scaffolds.Agent("Agent", "Contoso Helpdesk", SchemaName, AgentAuthoringModes.Classic, instructions: "Be helpful."), HostedCloud.Target(SchemaName), HostedCloud.Options(), _authentication, CancellationToken.None);
        var pushedCache = _files.TakeCache("Agent");
        await host.CloneAsync(new AgentCloneRequest(HostedCloud.Target(SchemaName, result.BotId), "Agent", new Dictionary<string, string>(), Array.Empty<string>()), _authentication, CancellationToken.None);

        Assert.Empty(result.Diagnostics);
        Assert.Contains("Named By The Service", pushedCache, StringComparison.Ordinal);
        Assert.Equal(pushedCache, _files.TakeCache("Agent"));
    }

    [Fact]
    public async Task AnAgentShapedLikeAgentD1_AttachedAndPushedFromItsFolder_AppliesTheAuthoredSettingsAndLeavesTheFolderAndCacheEqualToTheBot()
    {
        var files = new FileAccessorFactory();
        var root = new DirectoryPath(_root.Replace('\\', '/') + "/");
        var folder = root.GetChildDirectoryPath(Folder);
        var workspace = files.Create(folder);
        var synchronizer = new WorkspaceSynchronizer(new SyncMcsFileParser(LspProjectorService.Instance), files, _cloud.Island.Service.Object, new TestSyncProgress(new List<string>()), new LspComponentPathResolver());
        var contexts = new OperationContextProvider();
        var snapshot = AgentD1();
        WorkspaceSnapshotFiles.Write(files, root, snapshot);

        var attached = await new WorkspaceAttachService(synchronizer, synchronizer, synchronizer, contexts, files, TimeProvider.System).AttachAsync(new WorkspaceAttachRequest { WorkspaceFolder = folder, DataverseEndpoint = HostedCloud.Organization, EnvironmentId = HostedCloud.EnvironmentId, TenantId = HostedCloud.TenantId, DisplayName = "Contoso Helpdesk", SchemaName = ClassicAgentFixtures.AgentD1Schema }, _cloud.Cloud.Client.Object, CancellationToken.None);
        await new WorkspacePushService(synchronizer, synchronizer, contexts, files, TimeProvider.System).PushAsync(new WorkspacePushRequest { WorkspaceFolder = folder, Overwrite = true }, _cloud.Cloud.Client.Object, CancellationToken.None);

        var entity = _cloud.Island.EntityOf(attached.AgentId)!;
        Assert.Equal(Expected(Text(snapshot, SettingsFile), "empty-1.0.0"), SettingsOf(entity));
        Assert.Null(entity.PublishedOn);
        Assert.Equal(SettingsOf(entity), SettingsOf(CodeSerializer.Deserialize<BotEntity>(await workspace.ReadStringAsync(WorkspaceSynchronizer.SettingsPath, CancellationToken.None))!));
        Assert.Equal(SettingsOf(entity), SettingsOf(((BotDefinition)WorkspaceSynchronizer.ReadCloudCacheSnapshot(workspace)!).Entity!));
        Assert.Empty((await synchronizer.GetLocalChangesAsync(folder, await synchronizer.ReadWorkspaceDefinitionAsync(folder, CancellationToken.None), CancellationToken.None)).Item2);
    }

    [Theory]
    [MemberData(nameof(CopiesRenamedOnDisk))]
    public async Task AnAgentClonedToDiskUnderANewDisplayName_KeepsTheSourceNameInItsCacheAndItsFirstPushRenamesTheBot(AuthoringShape shape, string displayName)
    {
        var botId = _cloud.SeedAgent(SchemaName, "Contoso Helpdesk", shape, PushComponentCollectionExclusionTests.Topic($"{SchemaName}.topic.Greeting", "Greeting"));
        var clone = await CloneToDiskAsync(botId);

        AgentWorkspaceIdentity.DeclareDisplayName(clone.Workspace, displayName);

        Assert.Equal("Contoso Helpdesk", CachedDisplayNameOf(clone));
        Assert.Equal("entity", Assert.Single((await clone.Synchronizer.GetLocalChangesAsync(clone.Folder, await clone.Synchronizer.ReadWorkspaceDefinitionAsync(clone.Folder, CancellationToken.None), CancellationToken.None)).Item2).SchemaName);
        await clone.PushAsync(_cloud.Cloud.Client.Object);
        Assert.Equal(displayName, Assert.Single(_cloud.Island.SavedChanges, saved => saved.Bot is not null).Bot!.DisplayName);
        Assert.Equal(displayName, _cloud.Island.EntityOf(botId)!.DisplayName);
        Assert.Equal(displayName, CachedDisplayNameOf(clone));
        Assert.Equal(SchemaName, AgentWorkspaceIdentity.TryRead(clone.Workspace)!.SchemaName);
    }

    [Theory]
    [InlineData(AuthoringShape.Classic)]
    [InlineData(AuthoringShape.CliCopilot)]
    public async Task ADiskPush_ThatWouldRenameTheBotToADisplayNameLongerThanTheLimit_IsRefusedBeforeWritingAnything(AuthoringShape shape)
    {
        var botId = _cloud.SeedAgent(SchemaName, "Contoso Helpdesk", shape, PushComponentCollectionExclusionTests.Topic($"{SchemaName}.topic.Greeting", "Greeting"));
        var clone = await CloneToDiskAsync(botId);
        await clone.Workspace.WriteAsync(WorkspaceSynchronizer.SettingsPath, Encoding.UTF8.GetBytes(DeclaredDisplayName.Replace(await clone.Workspace.ReadStringAsync(WorkspaceSynchronizer.SettingsPath, CancellationToken.None), "displayName: " + new string('a', 43), 1)), CancellationToken.None);

        Assert.Equal("settings.mcs.yml: An agent display name can hold at most 42 characters, and this one holds 43.", Assert.Single((await Assert.ThrowsAsync<WorkspaceValidationException>(() => clone.PushAsync(_cloud.Cloud.Client.Object))).Diagnostics).ToString());
        Assert.Empty(_cloud.Island.SavedChanges);
        Assert.Equal("Contoso Helpdesk", _cloud.Island.EntityOf(botId)!.DisplayName);
        Assert.Equal("Contoso Helpdesk", CachedDisplayNameOf(clone));
    }

    [Theory]
    [InlineData(AuthoringShape.Classic)]
    [InlineData(AuthoringShape.CliCopilot)]
    public async Task ADiskPush_OfABotThatAlreadyCarriesADisplayNameLongerThanTheLimit_KeepsPushingUnderThatName(AuthoringShape shape)
    {
        var legacyName = new string('L', 50);
        var botId = _cloud.SeedAgent(SchemaName, legacyName, shape, PushComponentCollectionExclusionTests.Topic($"{SchemaName}.topic.Greeting", "Greeting"));
        var clone = await CloneToDiskAsync(botId);

        await clone.PushAsync(_cloud.Cloud.Client.Object);

        Assert.Equal(legacyName, AgentWorkspaceIdentity.TryRead(clone.Workspace)!.DeclaredDisplayName);
        Assert.Equal(legacyName, _cloud.Island.EntityOf(botId)!.DisplayName);
        Assert.Equal(legacyName, CachedDisplayNameOf(clone));
    }

    [Fact]
    public async Task ADiskPush_OfAClassicAgentWithoutASettingsFile_HandsTheServiceTheBotsCurrentSettingsAndLeavesTheBotAsItWas()
    {
        var botId = _cloud.SeedAgent(SchemaName, "Contoso Helpdesk", AuthoringShape.Classic, PushComponentCollectionExclusionTests.Topic($"{SchemaName}.topic.Greeting", "Greeting"));
        var entity = _cloud.Island.EntityOf(botId)!;
        var clone = await CloneToDiskAsync(botId);
        clone.Workspace.Delete(WorkspaceSynchronizer.SettingsPath);

        var result = await clone.PushAsync(_cloud.Cloud.Client.Object);

        Assert.Equal(SettingsOf(entity), SettingsOf(Assert.IsType<BotDefinition>(result.Definition).Entity!));
        Assert.DoesNotContain(_cloud.Island.SavedChanges, changes => changes.Bot is not null);
        Assert.Same(entity, _cloud.Island.EntityOf(botId));
    }

    [Theory]
    [InlineData(AgentAuthoringModes.Classic)]
    [InlineData(AgentAuthoringModes.CliCopilot)]
    public async Task PushAsync_OfSettingsThatDeclareNoDisplayName_SendsTheBotWithoutANameInsteadOfItsSchemaName(string authoringMode)
    {
        var host = _cloud.CreateHost(_files);
        var scaffolded = Scaffolds.Agent("Agent", "Contoso Helpdesk", SchemaName, authoringMode, instructions: "Be helpful.");
        var botId = (await host.PushAsync(scaffolded, HostedCloud.Target(SchemaName), HostedCloud.Options(), _authentication, CancellationToken.None)).BotId;
        var saves = _cloud.Island.SavedChanges.Count;
        var unnamed = With(scaffolded, "Agent/settings.mcs.yml", string.Join("\n", Text(scaffolded, "Agent/settings.mcs.yml").Split('\n').Where(line => !line.StartsWith("displayName:", StringComparison.Ordinal))));

        await host.PushAsync(unnamed, HostedCloud.Target(SchemaName, botId), HostedCloud.Options(), _authentication, CancellationToken.None);

        Assert.Null((await host.ReadIdentityAsync(unnamed, CancellationToken.None))!.DeclaredDisplayName);
        Assert.Null(Assert.Single(_cloud.Island.SavedChanges.Skip(saves), changes => changes.Bot is not null).Bot!.DisplayName);
    }

    [Theory]
    [InlineData(AgentAuthoringModes.Classic)]
    [InlineData(AgentAuthoringModes.CliCopilot)]
    public async Task PushAsync_OfSettingsThatDeclareADisplayNameLongerThanTheLimit_FailsBeforeWritingAnything(string authoringMode)
    {
        var host = _cloud.CreateHost(_files);
        var scaffolded = Scaffolds.Agent("Agent", "Contoso Helpdesk", SchemaName, authoringMode, instructions: "Be helpful.");
        var botId = (await host.PushAsync(scaffolded, HostedCloud.Target(SchemaName), HostedCloud.Options(), _authentication, CancellationToken.None)).BotId;
        var saves = _cloud.Island.SavedChanges.Count;
        var renamed = Declaring(scaffolded, new string('a', 43));

        var failure = await Assert.ThrowsAsync<AgentWorkspaceHostException>(() => host.PushAsync(renamed, HostedCloud.Target(SchemaName, botId), HostedCloud.Options(), _authentication, CancellationToken.None));

        Assert.Equal(AgentWorkspaceHostErrorCodes.PushFailed, failure.Code);
        Assert.Contains("settings.mcs.yml: An agent display name can hold at most 42 characters, and this one holds 43.", failure.Message, StringComparison.Ordinal);
        Assert.Equal("settings.mcs.yml: An agent display name can hold at most 42 characters, and this one holds 43.", Assert.Single(Assert.IsType<WorkspaceValidationException>(failure.InnerException).Diagnostics).ToString());
        Assert.Equal(AgentPushStages.Components, Assert.Single(failure.Diagnostics).Subject);
        Assert.Equal(saves, _cloud.Island.SavedChanges.Count);
        Assert.Equal("Contoso Helpdesk", _cloud.Island.EntityOf(Guid.Parse(botId))!.DisplayName);
    }

    internal static string SettingsOf(BotEntity entity)
    {
        using (YamlSerializationContext.UseYamlPassThroughSerializationContext())
        {
            return JsonSerializer.Serialize(entity.WithOnlyAuthoredSettingsProperties(), ElementSerializer.CreateOptions());
        }
    }

    internal static string Expected(string authoredSettings, string template) => SettingsOf(CodeSerializer.Deserialize<BotEntity>(authoredSettings.Replace("template: default-2.1.0", $"template: {template}", StringComparison.Ordinal))!);

    internal static string WithAuthoredPublishedOn(string settings) => settings.TrimEnd('\n') + "\n" + AuthoredPublishedOn;

    private static AgentWorkspaceSnapshot AgentD1() => With(ClassicAgentFixtures.AgentD1(Folder), SettingsFile, WithAuthoredPublishedOn(Text(ClassicAgentFixtures.AgentD1(Folder), SettingsFile)));

    private static string? CachedDisplayNameOf(DiskClone clone) => ((BotDefinition)WorkspaceSynchronizer.ReadCloudCacheSnapshot(clone.Workspace)!).Entity!.DisplayName;

    private async Task<DiskClone> CloneToDiskAsync(Guid botId)
    {
        var files = new FileAccessorFactory();
        var folder = new DirectoryPath(_root.Replace('\\', '/') + "/").GetChildDirectoryPath("Contoso Copy");
        var synchronizer = new WorkspaceSynchronizer(new SyncMcsFileParser(LspProjectorService.Instance), files, _cloud.Island.Service.Object, new TestSyncProgress(new List<string>()), new LspComponentPathResolver());
        var contexts = new OperationContextProvider();
        var syncInfo = await WorkspaceCloneSequence.ForAgentAsync(_cloud.Cloud.Client.Object, botId, HostedCloud.Organization, HostedCloud.EnvironmentId, HostedCloud.TenantId, CancellationToken.None);
        await WorkspaceCloneSequence.CloneAsync(synchronizer, new[] { new KeyValuePair<AuthoringOperationContextBase, DirectoryPath>(await contexts.GetAsync(syncInfo), folder) }, syncInfo, _cloud.Cloud.Client.Object, CancellationToken.None);

        return new DiskClone(files, folder, files.Create(folder), synchronizer, contexts);
    }

    internal static Guid Published(HostedCloud cloud, Guid botId)
    {
        var builder = cloud.Island.EntityOf(botId)!.ToBuilder();
        builder.PublishedOn = BotPublishedOn;
        cloud.Island.SetEntity(botId, builder.Build());

        return botId;
    }

    private Task<AgentPushResult> PushAsync(AgentWorkspaceSnapshot snapshot, string? botId = null) => _cloud.CreateHost(_files).PushAsync(snapshot, HostedCloud.Target(ClassicAgentFixtures.AgentD1Schema, botId), HostedCloud.Options(), _authentication, CancellationToken.None);

    private sealed record DiskClone(FileAccessorFactory Files, DirectoryPath Folder, IFileAccessor Workspace, WorkspaceSynchronizer Synchronizer, OperationContextProvider Contexts)
    {
        public Task<WorkspacePushResult> PushAsync(ISyncDataverseClient dataverseClient) => new WorkspacePushService(Synchronizer, Synchronizer, Contexts, Files, TimeProvider.System).PushAsync(new WorkspacePushRequest { WorkspaceFolder = Folder }, dataverseClient, CancellationToken.None);
    }
}
