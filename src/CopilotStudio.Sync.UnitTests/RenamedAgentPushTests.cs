// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Hosting;
using Moq;
using Xunit;
using static Microsoft.CopilotStudio.Sync.UnitTests.AgentWorkspaceHostPushTests;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public sealed class RenamedAgentPushTests : IDisposable
{
    private const string RenamedSchema = "cr468_amse2ed10406";

    private const string OtherSchema = "cr468_amse2ed20817";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "renamed-push-" + Guid.NewGuid().ToString("N"));

    private readonly HostedCloud _cloud = new();

    private readonly RecordingAuthProvider _authentication = new();

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task AnAgentShapedLikeAgentD1_AttachedAndPushedFromItsFolderUnderAnotherSchemaName_NamesItsChildAgentAndVariablesAfterTheBotAndLeavesTheFolderReadyForTheNextPush()
    {
        var files = new FileAccessorFactory();
        var root = new DirectoryPath(_root.Replace('\\', '/') + "/");
        var folder = root.GetChildDirectoryPath("Agent D1");
        var workspace = files.Create(folder);
        var synchronizer = new WorkspaceSynchronizer(new SyncMcsFileParser(LspProjectorService.Instance), files, _cloud.Island.Service.Object, new TestSyncProgress(new List<string>()), new LspComponentPathResolver());
        var contexts = new OperationContextProvider();
        var attach = new WorkspaceAttachService(synchronizer, synchronizer, synchronizer, contexts, files, TimeProvider.System);
        var push = new WorkspacePushService(synchronizer, synchronizer, contexts, files, TimeProvider.System);
        WorkspaceSnapshotFiles.Write(files, root, AgentD1WithVariable("Agent D1"));

        var attached = await attach.AttachAsync(Request(folder, RenamedSchema, allowRetarget: false), _cloud.Cloud.Client.Object, CancellationToken.None);
        await push.PushAsync(new WorkspacePushRequest { WorkspaceFolder = folder, Overwrite = true }, _cloud.Cloud.Client.Object, CancellationToken.None);

        Assert.Equal(RenamedAgentD1(RenamedSchema), SchemaNamesOf(attached.AgentId));
        Assert.Equal(new[] { $"{RenamedSchema}.region" }, _cloud.Island.EnvironmentVariablesInserted.Distinct(StringComparer.Ordinal));
        await AssertTheFolderIsNamedAfterAsync(workspace, RenamedSchema);

        var inserted = _cloud.Island.Inserted.Count;
        await push.PushAsync(new WorkspacePushRequest { WorkspaceFolder = folder, Overwrite = true }, _cloud.Cloud.Client.Object, CancellationToken.None);

        Assert.Equal(inserted, _cloud.Island.Inserted.Count);
        Assert.Equal(RenamedAgentD1(RenamedSchema), SchemaNamesOf(attached.AgentId));
        await AssertTheFolderIsNamedAfterAsync(workspace, RenamedSchema);

        var moved = await attach.AttachAsync(Request(folder, OtherSchema, allowRetarget: true), _cloud.Cloud.Client.Object, CancellationToken.None);
        await push.PushAsync(new WorkspacePushRequest { WorkspaceFolder = folder, Overwrite = true }, _cloud.Cloud.Client.Object, CancellationToken.None);

        Assert.NotEqual(attached.AgentId, moved.AgentId);
        Assert.Equal(RenamedAgentD1(OtherSchema), SchemaNamesOf(moved.AgentId));
        Assert.Equal(RenamedAgentD1(RenamedSchema), SchemaNamesOf(attached.AgentId));
        Assert.Contains($"{OtherSchema}.region", _cloud.Island.EnvironmentVariablesInserted);
        await AssertTheFolderIsNamedAfterAsync(workspace, OtherSchema);
    }

    [Fact]
    public async Task AnAgentShapedLikeAgentD1_PushedByTheHostOnDiskUnderAnotherSchemaName_LandsLikeTheSamePushInMemory()
    {
        var onDisk = new HostedCloud();
        var snapshot = AgentD1WithVariable("Agent D1");

        var created = await onDisk.CreateHost(new FileAccessorFactory()).PushAsync(snapshot, HostedCloud.Target(RenamedSchema), HostedCloud.Options(), _authentication, CancellationToken.None);
        var inMemory = await _cloud.CreateHost().PushAsync(snapshot, HostedCloud.Target(RenamedSchema), HostedCloud.Options(), _authentication, CancellationToken.None);

        Assert.Equal(RenamedAgentD1(RenamedSchema), onDisk.Island.ComponentsOf(Guid.Parse(created.BotId)).Select(component => component.SchemaNameString).OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal(SchemaNamesOf(Guid.Parse(inMemory.BotId)), onDisk.Island.ComponentsOf(Guid.Parse(created.BotId)).Select(component => component.SchemaNameString).OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal(_cloud.Island.EnvironmentVariablesInserted.Distinct(StringComparer.Ordinal), onDisk.Island.EnvironmentVariablesInserted.Distinct(StringComparer.Ordinal));
    }

    [Fact]
    public async Task PushAsync_OfACliAgentUnderAnotherSchemaName_NamesItsSkillAndVariableAfterTheBotAndRedeploysInPlace()
    {
        var snapshot = Scaffolds.Agent("Agent", "Contoso Helpdesk", SchemaName, AgentAuthoringModes.CliCopilot);
        snapshot = With(snapshot, "Agent/behaviors/get-us-weather/skill.mcs.yml", $"mcs.metadata:\n  componentName: get-us-weather\n  schemaName: {SchemaName}.skill.get-us-weather\nkind: InlineAgentSkill\n");
        snapshot = With(snapshot, "Agent/behaviors/get-us-weather/SKILL.md", "---\nname: get-us-weather\ndescription: Gets the weather.\n---\nCall the weather tool.\n");
        snapshot = With(snapshot, $"Agent/environmentvariables/{SchemaName}.channel.mcs.yml", ClassicAgentFixtures.Variable($"{SchemaName}.channel", "displayName: Channel\n"));

        var created = await _cloud.CreateHost().PushAsync(snapshot, HostedCloud.Target(RenamedSchema), HostedCloud.Options(), _authentication, CancellationToken.None);

        Assert.Equal(created.BotId, (await _cloud.CreateHost().PushAsync(snapshot, HostedCloud.Target(RenamedSchema, created.BotId), HostedCloud.Options(), _authentication, CancellationToken.None)).BotId);
        Assert.Single(_cloud.CreatedAgents);
        Assert.Equal(new[] { $"{RenamedSchema}.skill.get-us-weather" }, SchemaNamesOf(Guid.Parse(created.BotId)));
        Assert.Equal(new[] { $"{RenamedSchema}.channel" }, _cloud.Island.EnvironmentVariablesInserted.Distinct(StringComparer.Ordinal));
        Assert.Equal(_cloud.Island.Inserted.Count, _cloud.Island.Inserted.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task PushAsync_OfAClassicAgentAndItsCollectionUnderAnotherSchemaName_RenamesTheAgentsOwnComponentsAndLeavesTheCollectionsAlone()
    {
        var agentComponents = SchemaNamesOf(Guid.Parse((await _cloud.CreateHost().PushAsync(ClassicAgentFixtures.Cc1(), HostedCloud.Target(RenamedSchema), HostedCloud.Options(), _authentication, CancellationToken.None)).BotId));
        Assert.Contains($"{RenamedSchema}.GlobalVariableComponent.UserCountry", agentComponents);
        Assert.Contains($"{RenamedSchema}.topic.Greeting", agentComponents);
        Assert.DoesNotContain(agentComponents, name => name.StartsWith(ClassicAgentFixtures.Cc1Schema + ".", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(new[] { $"{ClassicAgentFixtures.Cc1Schema}.topic.ConversationStart", $"{ClassicAgentFixtures.Cc1Schema}.topic.Goodbye" }, SchemaNamesOf(_cloud.CollectionIdOf(ClassicAgentFixtures.Cc1CollectionSchema)));
    }

    [Fact]
    public async Task PushAsync_UnderAnotherSchemaName_OfAChildAgentLinkedToAThirdAgentsSchema_StillRefusesItAtTheComponentsStage()
    {
        var snapshot = With(ClassicAgentFixtures.AgentD1("Agent D1"), "Agent D1/agents/Agent child 1/agent.mcs.yml", "mcs.metadata:\n  componentName: Agent child 1\n  schemaName: cr999_other.agent.Agent\nkind: AgentDialog\nbeginDialog:\n  kind: OnToolSelected\n  id: main\n  description: Agent Child 1 Description\n\nsettings: {}\ninputType: {}\noutputType: {}\n");

        var failure = await Assert.ThrowsAsync<AgentWorkspaceHostException>(() => _cloud.CreateHost().PushAsync(snapshot, HostedCloud.Target(RenamedSchema), HostedCloud.Options(), _authentication, CancellationToken.None));

        Assert.Equal((AgentWorkspaceHostErrorCodes.PushFailed, AgentSyncDiagnosticCodes.PushStageFailed, AgentPushStages.Components), (failure.Code, Assert.Single(failure.Diagnostics).Code, failure.Diagnostics[0].Subject));
        Assert.Contains("cr999_other.agent.Agent", Assert.IsType<InvalidOperationException>(failure.InnerException).Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_cloud.Island.Inserted, name => name.Contains(".agent.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PushAsync_UnderAnotherSchemaName_WhenTheSnapshotAlreadyHoldsAFileUnderARenamedName_StopsBeforePushingAnyComponent()
    {
        var snapshot = With(AgentD1WithVariable("Agent D1"), $"Agent D1/environmentvariables/{RenamedSchema}.region.mcs.yml", ClassicAgentFixtures.Variable($"{RenamedSchema}.region", "displayName: Region\n"));

        var failure = await Assert.ThrowsAsync<AgentWorkspaceHostException>(() => _cloud.CreateHost().PushAsync(snapshot, HostedCloud.Target(RenamedSchema), HostedCloud.Options(), _authentication, CancellationToken.None));

        Assert.Equal((AgentWorkspaceHostErrorCodes.PushFailed, AgentPushStages.Agent), (failure.Code, Assert.Single(failure.Diagnostics).Subject));
        Assert.Equal($"environmentvariables/{ClassicAgentFixtures.AgentD1Schema}.region.mcs.yml", Assert.Single(Assert.IsType<WorkspaceValidationException>(failure.InnerException!.InnerException).Diagnostics).FilePath);
        Assert.Empty(_cloud.Island.Inserted);
        Assert.Empty(_cloud.Island.EnvironmentVariablesInserted);
    }

    private static AgentWorkspaceSnapshot AgentD1WithVariable(string folder) => With(ClassicAgentFixtures.AgentD1(folder), $"{folder}/environmentvariables/{ClassicAgentFixtures.AgentD1Schema}.region.mcs.yml", ClassicAgentFixtures.Variable($"{ClassicAgentFixtures.AgentD1Schema}.region", "displayName: Region\n"));

    private static string[] RenamedAgentD1(string schemaName) => ClassicAgentFixtures.AgentD1SchemaNames.Select(name => schemaName + name.Substring(ClassicAgentFixtures.AgentD1Schema.Length)).ToArray();

    private static WorkspaceAttachRequest Request(DirectoryPath folder, string schemaName, bool allowRetarget) => new() { WorkspaceFolder = folder, DataverseEndpoint = HostedCloud.Organization, EnvironmentId = HostedCloud.EnvironmentId, TenantId = HostedCloud.TenantId, DisplayName = "Agent D1", SchemaName = schemaName, AllowRetarget = allowRetarget, PersistRetargetBackup = true };

    private static async Task AssertTheFolderIsNamedAfterAsync(IFileAccessor workspace, string schemaName)
    {
        Assert.Equal(schemaName, AgentWorkspaceIdentity.TryRead(workspace)?.SchemaName);
        Assert.Equal(new[] { new KeyValuePair<string, string>("Agent child 1", $"{schemaName}.agent.Agent") }, ChildAgentLink.ReadSchemaLinks(workspace));
        Assert.Equal(new[] { $"environmentvariables/{schemaName}.region.mcs.yml" }, workspace.ListFiles("environmentvariables").Select(path => path.ToString()));
        Assert.Contains($"schemaName: {schemaName}.region", await workspace.ReadStringAsync(new AgentFilePath($"environmentvariables/{schemaName}.region.mcs.yml"), CancellationToken.None), StringComparison.Ordinal);
    }

    private string[] SchemaNamesOf(Guid identity) => _cloud.Island.ComponentsOf(identity).Select(component => component.SchemaNameString).OrderBy(name => name, StringComparer.Ordinal).ToArray();
}
