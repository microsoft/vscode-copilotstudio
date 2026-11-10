// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text;
using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.Platform.Content;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Microsoft.CopilotStudio.Sync.Hosting;
using Moq;
using Xunit;
using static Microsoft.CopilotStudio.Sync.Dataverse.SyncDataverseClient;
using static Microsoft.CopilotStudio.Sync.UnitTests.KnowledgeFileTestFixtures;
using ProductionFileAccessorFactory = Microsoft.CopilotStudio.McsCore.InMemoryFileAccessorFactory;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class NoDiskCollection
{
    public const string Name = "NoDisk";
}

[Collection(NoDiskCollection.Name)]
public class InMemoryNoDiskTests : IDisposable
{
    private const string Definition = """
    {"properties":{"connectionReferences":{"shared_teams":{"api":{"name":"shared_teams"},"connection":{"connectionReferenceLogicalName":"new_teams"}}}}}
    """;

    private static readonly string[] TemporaryFolderVariables = { "TEMP", "TMP", "TMPDIR" };

    private readonly string _sandbox = Path.Combine(Path.GetTempPath(), "mcs-no-disk-" + Guid.NewGuid().ToString("N"));

    private readonly string _savedWorkingFolder = Environment.CurrentDirectory;

    private readonly Dictionary<string, string?> _savedVariables = TemporaryFolderVariables.ToDictionary(name => name, Environment.GetEnvironmentVariable);

    private readonly string _temporaryFolder;

    private readonly string _workingFolder;

    private readonly ConcurrentQueue<string> _touched = new();

    private readonly FileSystemWatcher _watcher;

    public InMemoryNoDiskTests()
    {
        _temporaryFolder = Directory.CreateDirectory(Path.Combine(_sandbox, "temp")).FullName;
        _workingFolder = Directory.CreateDirectory(Path.Combine(_sandbox, "work")).FullName;
        _watcher = new FileSystemWatcher(_sandbox) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
        _watcher.Created += (_, change) => _touched.Enqueue(change.FullPath);
        _watcher.Changed += (_, change) => _touched.Enqueue(change.FullPath);
        _watcher.Deleted += (_, change) => _touched.Enqueue(change.FullPath);
        _watcher.Renamed += (_, change) => _touched.Enqueue(change.FullPath);
        _watcher.EnableRaisingEvents = true;

        foreach (var name in TemporaryFolderVariables)
        {
            Environment.SetEnvironmentVariable(name, _temporaryFolder);
        }

        Environment.CurrentDirectory = _workingFolder;
    }

    public void Dispose()
    {
        Environment.CurrentDirectory = _savedWorkingFolder;

        foreach (var (name, value) in _savedVariables)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        _watcher.Dispose();
        Directory.Delete(_sandbox, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task AnAgentClonedPushedAndTransferredInMemory_LeavesTheDiskUntouched()
    {
        Assert.StartsWith(_temporaryFolder, Path.GetTempPath(), StringComparison.OrdinalIgnoreCase);
        using var factory = new ProductionFileAccessorFactory(requireSession: true);
        using var session = factory.LeaseWorkspace(new DirectoryPath("operation/"));
        var workspace = session.Root.GetChildDirectoryPath("Agent");
        var island = new Mock<IIslandControlPlaneService>();
        var synchronizer = new WorkspaceSynchronizer(new SyncMcsFileParser(LspProjectorService.Instance), factory, island.Object, new TestSyncProgress(new List<string>()), new LspComponentPathResolver());
        var operationContext = ComponentWriterDefensiveTests.CreateMockOperationContext();
        var syncInfo = new AgentSyncInfo { AgentId = Guid.NewGuid() };
        var knowledge = CreateFileComponent("cr1.file.Policies", "Policies.pdf", Guid.NewGuid());
        var greeting = PushComponentCollectionExclusionTests.Topic("cr1.topic.Greeting", "Greeting");
        var farewell = PushComponentCollectionExclusionTests.Topic("cr1.topic.Farewell", "Farewell");
        var cloudFiles = new ConcurrentDictionary<Guid, byte[]> { [knowledge.Id.Value] = Encoding.UTF8.GetBytes("cloud policies") };
        var dataverse = CloudBackedDataverse(cloudFiles);
        dataverse.Setup(client => client.DownloadAllWorkflowsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>())).ReturnsAsync(new[] { new WorkflowMetadata { WorkflowId = Guid.NewGuid(), Name = "Notify", ClientData = Definition } });
        dataverse.Setup(client => client.DownloadAllAIPromptsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<AIPromptMetadata>());
        island.Setup(service => service.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new PvaComponentChangeSet(new BotComponentChange[] { new BotComponentInsert(knowledge), new BotComponentInsert(greeting) }, CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: cr1")!, "token-1"));
        island.Setup(service => service.SaveChangesAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<PvaComponentChangeSet>(), It.IsAny<CancellationToken>())).ReturnsAsync(new PvaComponentChangeSet(new BotComponentChange[] { new BotComponentInsert(farewell) }, null, "token-2"));

        await synchronizer.CloneChangesAsync(workspace, new ReferenceTracker(), operationContext, dataverse.Object, syncInfo, CancellationToken.None);

        var accessor = factory.Create(workspace);
        var knowledgeFile = new AgentFilePath(Assert.Single(await synchronizer.ListKnowledgeFilesAsync(workspace, CancellationToken.None)).RelativePath);
        Assert.Equal(cloudFiles[knowledge.Id.Value], await accessor.ReadBytesAsync(knowledgeFile, CancellationToken.None));
        Assert.Equal(KnowledgeHashOf(cloudFiles[knowledge.Id.Value]), ReadKnowledgeSyncState(accessor)[knowledge.Id.Value.ToString("N")]);
        Assert.Contains(WorkspaceSynchronizer.ReadCloudCacheSnapshot(accessor)!.Components, component => component.SchemaNameString == greeting.SchemaNameString);
        Assert.Single(WorkflowWorkspace.FindAll(accessor));
        Assert.Equal(WorkspaceLayout.ClassicMcs, AgentClassifier.DetectWorkspaceLayout(accessor));

        await accessor.WriteAsync(knowledgeFile, Encoding.UTF8.GetBytes("local policies"), CancellationToken.None);

        Assert.Equal(1, (await synchronizer.PushChangesetAsync(workspace, operationContext, new PvaComponentChangeSet(new BotComponentChange[] { new BotComponentInsert(farewell) }, null, "token-1"), dataverse.Object, syncInfo.AgentId, null, ImmutableArray<AIPromptMetadata>.Empty, CancellationToken.None, uploadAllKnowledgeFiles: true)).UploadedKnowledgeFileCount);
        Assert.Equal(Encoding.UTF8.GetBytes("local policies"), cloudFiles[knowledge.Id.Value]);
        Assert.Equal(KnowledgeHashOf(cloudFiles[knowledge.Id.Value]), ReadKnowledgeSyncState(accessor)[knowledge.Id.Value.ToString("N")]);
        Assert.Contains(WorkspaceSynchronizer.ReadCloudCacheSnapshot(accessor)!.Components, component => component.SchemaNameString == farewell.SchemaNameString);
        Assert.Equal("token-2", await accessor.ReadStringAsync(new AgentFilePath(".mcs/changetoken.txt"), CancellationToken.None));

        Assert.Empty(await synchronizer.UploadKnowledgeFilesAsync(workspace, dataverse.Object, CancellationToken.None));
        cloudFiles[knowledge.Id.Value] = Encoding.UTF8.GetBytes("cloud revision");
        Assert.Single(await synchronizer.DownloadKnowledgeFilesAsync(workspace, dataverse.Object, schemaNames: null, CancellationToken.None));
        Assert.Equal(cloudFiles[knowledge.Id.Value], await accessor.ReadBytesAsync(knowledgeFile, CancellationToken.None));

        AssertTheDiskIsUntouched();
    }

    [Fact]
    public void AgentsScaffoldedInMemory_LeaveTheDiskUntouched()
    {
        using var factory = new ProductionFileAccessorFactory(requireSession: true);
        using var session = factory.LeaseWorkspace(new DirectoryPath("operation/"));

        foreach (var authoringMode in new[] { AgentAuthoringModes.Classic, AgentAuthoringModes.CliCopilot })
        {
            var workspace = factory.Create(session.Root.GetChildDirectoryPath(authoringMode));
            var created = AgentScaffolder.Scaffold(workspace, authoringMode, "Contoso", "con_contoso", "Be helpful.");

            Assert.NotEmpty(created);
            Assert.All(created, path => Assert.True(workspace.Exists(new AgentFilePath(path))));
        }

        AssertTheDiskIsUntouched();
    }

    [Fact]
    public async Task AWorkspaceBoundAndActivatedInMemory_LeavesTheDiskUntouched()
    {
        using var factory = new ProductionFileAccessorFactory(requireSession: true);
        using var session = factory.LeaseWorkspace(new DirectoryPath("operation/"));

        var outcome = await WorkflowActivationPassTests.BindAndActivateInMemoryAsync(factory, session.Root);

        Assert.Null(outcome.Result.Failure);
        Assert.Single(outcome.Result.Activated);
        AssertTheDiskIsUntouched();
    }

    [Fact]
    public async Task ACollectionAndItsAgentPushedInMemory_LeaveTheDiskUntouched()
    {
        using var factory = new ProductionFileAccessorFactory(requireSession: true);
        using var session = factory.LeaseWorkspace(new DirectoryPath("operation/"));

        Assert.Single((await ComponentCollectionPushSequenceTests.PushCollectionThenAgentInMemoryAsync(factory, session.Root)).Result.Collections);
        AssertTheDiskIsUntouched();
    }

    [Fact]
    public async Task WorkspacesAttachedAndRetargetedInMemory_LeaveTheDiskUntouched()
    {
        using var factory = new ProductionFileAccessorFactory(requireSession: true);
        using var session = factory.LeaseWorkspace(new DirectoryPath("operation/"));

        var created = await WorkspaceAttachServiceTests.AttachInMemoryAsync(factory, session.Root.GetChildDirectoryPath("Created/"), retarget: false);
        var retargeted = await WorkspaceAttachServiceTests.AttachInMemoryAsync(factory, session.Root.GetChildDirectoryPath("Retargeted/"), retarget: true);

        Assert.True(created.Result.AgentCreated);
        Assert.True(retargeted.Result.Retargeted);
        AssertTheDiskIsUntouched();
    }

    [Fact]
    public async Task KnowledgeTransferThroughAClientThatCannotStream_IsRefusedWithoutStagingOnDisk()
    {
        Assert.StartsWith(_temporaryFolder, Path.GetTempPath(), StringComparison.OrdinalIgnoreCase);
        using var factory = new ProductionFileAccessorFactory(requireSession: true);
        using var session = factory.LeaseWorkspace(new DirectoryPath("operation/"));
        var workspace = session.Root.GetChildDirectoryPath("Agent");
        var synchronizer = new WorkspaceSynchronizer(new SyncMcsFileParser(LspProjectorService.Instance), factory, new Mock<IIslandControlPlaneService>().Object, new TestSyncProgress(new List<string>()), new LspComponentPathResolver());
        var accessor = factory.Create(workspace);
        WriteBotCloudCache(accessor, CreateFileComponent("cr1.file.Policies", "Policies.pdf", Guid.NewGuid()));
        await accessor.WriteAsync(new AgentFilePath(Assert.Single(await synchronizer.ListKnowledgeFilesAsync(workspace, CancellationToken.None)).RelativePath), "local policies", CancellationToken.None);
        var clientThatCannotStream = new Mock<ISyncDataverseClient>(MockBehavior.Strict);

        var download = await Assert.ThrowsAsync<InvalidOperationException>(() => synchronizer.DownloadKnowledgeFilesAsync(workspace, clientThatCannotStream.Object, schemaNames: null, CancellationToken.None));
        var upload = await Assert.ThrowsAsync<InvalidOperationException>(() => synchronizer.UploadKnowledgeFilesAsync(workspace, clientThatCannotStream.Object, CancellationToken.None));

        Assert.Contains(nameof(IStreamingKnowledgeFileClient), download.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(IStreamingKnowledgeFileClient), upload.Message, StringComparison.Ordinal);
        Assert.Empty(ReadKnowledgeSyncState(accessor));
        AssertTheDiskIsUntouched();
    }

    [Fact]
    public async Task StandaloneWorkflowsScaffoldedClonedPushedAndDeletedInMemory_LeaveTheDiskUntouched()
    {
        Assert.StartsWith(_temporaryFolder, Path.GetTempPath(), StringComparison.OrdinalIgnoreCase);
        using var factory = new ProductionFileAccessorFactory(requireSession: true);
        using var session = factory.LeaseWorkspace(new DirectoryPath("operation/"));
        var client = new FakeStandaloneWorkflowClient();
        var synchronizer = new StandaloneWorkflowSynchronizer(client, factory);
        var scaffoldedId = Guid.NewGuid();
        var scaffolded = factory.Create(session.Root.GetChildDirectoryPath("Scaffolded"));

        WorkflowScaffolder.Scaffold(scaffolded, "Scaffolded Flow", scaffoldedId);
        Assert.Equal(WorkflowProjectHost.UnpushedChanges, WorkflowWorkspace.InspectProject(scaffolded, scaffoldedId));
        await synchronizer.UpsertAsync(scaffolded, "env", CancellationToken.None);

        Assert.Equal(client.Stored(scaffoldedId)!.ClientData, StandaloneWorkflowCache.Read(scaffolded, scaffoldedId)!.ClientData);

        var clonedId = Guid.NewGuid();
        client.Seed(new WorkflowMetadata { WorkflowId = clonedId, Name = "My Flow", ClientData = Definition, StateCode = 0, StatusCode = 1 });
        var cloned = factory.Create(session.Root.GetChildDirectoryPath((await synchronizer.DownloadProjectAsync(clonedId, session.Root.ToString(), "env", CancellationToken.None)).ProjectFolder));
        await cloned.WriteAsync(WorkflowWorkspace.RequireSingle(cloned, "workflow").DefinitionPath, Definition.Replace("new_teams", "new_outlook"), CancellationToken.None);
        await synchronizer.UpsertAsync(cloned, "env", CancellationToken.None);

        Assert.Contains("new_outlook", client.Stored(clonedId)!.ClientData, StringComparison.Ordinal);
        Assert.Equal(client.Stored(clonedId)!.ClientData, StandaloneWorkflowCache.Read(cloned, clonedId)!.ClientData);

        await synchronizer.PullAsync(clonedId, cloned, CancellationToken.None);
        await synchronizer.DownloadAsync(clonedId, cloned, "env", CancellationToken.None);
        Assert.True(await synchronizer.DeleteAsync(clonedId, cloned, CancellationToken.None));

        Assert.Null(client.Stored(clonedId));
        Assert.Null(StandaloneWorkflowCache.Read(cloned, clonedId));
        AssertTheDiskIsUntouched();
    }

    [Fact]
    public async Task AnAgentScaffoldedClonedAndPushedAcrossEnvironmentsByTheHost_LeavesTheDiskUntouched()
    {
        Assert.StartsWith(_temporaryFolder, Path.GetTempPath(), StringComparison.OrdinalIgnoreCase);
        var source = new HostedCloud();
        var target = new HostedCloud();
        var authentication = new RecordingAuthProvider();
        var flowId = Guid.NewGuid();
        var connectorRowId = Guid.NewGuid();
        var sourceBotId = source.SeedAgent("con_source", "Contoso Helpdesk", PushComponentCollectionExclusionTests.Topic("con_source.topic.Greeting", "Greeting"));
        source.AddWorkflow(sourceBotId, new WorkflowMetadata { WorkflowId = flowId, Name = "Notify", ClientData = Definition, StateCode = 0, StatusCode = 1 });
        source.AddPrompt(sourceBotId, new AIPromptMetadata { AIModelId = Guid.NewGuid(), Name = "Summarize", CustomConfiguration = "{\"version\":\"1\"}" });
        target.Cloud.Client.Setup(client => client.GetConnectorsByInternalIdPrefixAsync("shared_cr123-5fcontoso-5f", It.IsAny<CancellationToken>())).ReturnsAsync(new[] { new CustomConnectorMetadata { ConnectorId = connectorRowId, ConnectorInternalId = "shared_cr123-5fcontoso-5ffedcba0987654321" } });

        var scaffolds = new[] { AgentAuthoringModes.Classic, AgentAuthoringModes.CliCopilot }.Select(mode => Scaffolds.Agent("Agent", "Contoso Helpdesk", "con_source", mode)).ToArray();
        var snapshot = AgentWorkspaceHostPushTests.With((await source.CreateHost().CloneAsync(new AgentCloneRequest(new AgentSyncTarget(HostedCloud.Organization, "env-source", HostedCloud.TenantId, sourceBotId.ToString(), "con_source", "Contoso Helpdesk", string.Empty), "Agent", new Dictionary<string, string>(), Array.Empty<string>()), authentication, CancellationToken.None)).Snapshot, "Agent/connectionreferences.mcs.yml", AgentWorkspaceHostPushTests.Declaration("con_contosoapi", "shared_cr123-5fcontoso-5f1234567890abcdef"));
        snapshot = AgentWorkspaceHostPushTests.With(snapshot, $"Agent/connectors/ContosoApi-{connectorRowId}/metadata.yml", $"{{\"connectorid\":\"{connectorRowId}\",\"name\":\"ContosoApi\",\"displayname\":\"Contoso API\",\"connectorinternalid\":\"shared_cr123-5fcontoso-5f1234567890abcdef\",\"openapidefinition\":\"connectors/ContosoApi-{connectorRowId}/openapidefinition.json\",\"connectortype\":0}}");
        snapshot = AgentWorkspaceHostPushTests.With(snapshot, $"Agent/connectors/ContosoApi-{connectorRowId}/openapidefinition.json", "{\"swagger\":\"2.0\"}");
        var pushed = await target.CreateHost().PushAsync(snapshot, HostedCloud.Target("con_target"), HostedCloud.Options(), authentication, CancellationToken.None);

        var targetBotId = target.AgentIdOf("con_target");
        Assert.All(scaffolds, scaffold => Assert.NotEmpty(scaffold.Files));
        Assert.True(pushed.Published);
        Assert.Contains(target.Island.ComponentsOf(targetBotId), component => component.SchemaNameString == "con_target.topic.Greeting");
        Assert.Equal("Notify", target.Cloud.Workflows[flowId].Name);
        Assert.Contains(pushed.FlowsLeftInDraft, flow => flow.WorkflowId == flowId.ToString());
        Assert.Equal("Summarize", Assert.Single(target.PromptsOf(targetBotId)).Name);
        target.Cloud.Client.Verify(client => client.UpsertConnectorAsync(It.Is<CustomConnectorMetadata>(connector => connector.ConnectorId == connectorRowId), It.IsAny<CancellationToken>()), Times.Once());
        Assert.Contains(("con_contosoapi", "/providers/Microsoft.PowerApps/apis/shared_cr123-5fcontoso-5ffedcba0987654321", (Guid?)connectorRowId), target.Cloud.EnsuredConnectionReferences);
        AssertTheDiskIsUntouched();
    }

    [Fact]
    public async Task AClassicAgentAndItsCollectionsPushedAndClonedByTheHost_LeaveTheDiskUntouched()
    {
        var host = new HostedCloud().CreateHost();
        var authentication = new RecordingAuthProvider();

        var pushed = await host.PushAsync(AgentWorkspaceHostPushTests.ScaffoldWithCollections(), HostedCloud.Target(AgentWorkspaceHostPushTests.SchemaName), HostedCloud.Options(), authentication, CancellationToken.None);
        var clone = await host.CloneAsync(new AgentCloneRequest(HostedCloud.Target(AgentWorkspaceHostPushTests.SchemaName, pushed.BotId), "Agent", new Dictionary<string, string>(), Array.Empty<string>()), authentication, CancellationToken.None);

        var validation = await host.ValidateAsync(clone.Snapshot, CancellationToken.None);

        Assert.True(validation.IsValid);
        Assert.Equal(new[] { "cree9_tools", "cree9_zeta" }, validation.DeclaredCollections.Keys.OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal(pushed.BotId, await host.FindAgentAsync(HostedCloud.Target(AgentWorkspaceHostPushTests.SchemaName), authentication, CancellationToken.None));
        AssertTheDiskIsUntouched();
    }

    [Fact]
    public async Task AStandaloneWorkflowPushedAndClonedByTheHost_LeavesTheDiskUntouched()
    {
        var host = new HostedCloud().CreateHost();
        var authentication = new RecordingAuthProvider();
        var workflowId = Guid.NewGuid();

        await host.PushWorkflowAsync(AgentWorkspaceHostWorkflowTests.Scaffold("Weather", workflowId), new WorkflowSyncTarget(HostedCloud.Organization, HostedCloud.EnvironmentId, HostedCloud.TenantId, null), HostedCloud.Options(), authentication, CancellationToken.None);
        var clone = await host.CloneWorkflowAsync(new WorkflowCloneRequest(new WorkflowSyncTarget(HostedCloud.Organization, HostedCloud.EnvironmentId, HostedCloud.TenantId, workflowId.ToString()), "Weather"), authentication, CancellationToken.None);

        Assert.Equal(workflowId.ToString(), clone.WorkflowId);
        Assert.NotEmpty(clone.Snapshot.Files);
        Assert.Equal(clone.WorkflowId, (await host.ValidateWorkflowAsync(clone.Snapshot, CancellationToken.None)).WorkflowId);
        AssertTheDiskIsUntouched();
    }

    [Fact]
    public async Task AClassicAgentShapedLikeAgentD1_PushedWithoutACloudCacheToANewBotAndRedeployed_LandsOnceTakesTheUpdatesAndLeavesTheDiskUntouched()
    {
        var cloud = new HostedCloud();
        var uploads = KnowledgeUploads(cloud);
        var authentication = new RecordingAuthProvider();
        var snapshot = ClassicAgentFixtures.AgentD1("Agent D1 Copy");
        Assert.DoesNotContain(snapshot.Files.Keys, path => path.Contains("/.mcs/", StringComparison.Ordinal));

        var created = await cloud.CreateHost().PushAsync(snapshot, HostedCloud.Target(ClassicAgentFixtures.AgentD1Schema), HostedCloud.Options(), authentication, CancellationToken.None);

        var botId = Guid.Parse(created.BotId);
        Assert.True(created.Published);
        Assert.Equal(ClassicAgentFixtures.AgentD1SchemaNames, SchemaNamesOf(cloud, botId));
        cloud.Cloud.Client.Verify(client => client.UpsertConnectorAsync(It.Is<CustomConnectorMetadata>(connector => connector.ConnectorId == ClassicAgentFixtures.ConnectorId), It.IsAny<CancellationToken>()), Times.Once());
        Assert.Equal(new[] { ClassicAgentFixtures.MsnWeatherReference, ClassicAgentFixtures.TeamsReference, ClassicAgentFixtures.WeatherConnectorReference }.OrderBy(name => name, StringComparer.Ordinal), cloud.Cloud.EnsuredLogicalNames.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal(new[] { ClassicAgentFixtures.ChildPromptId, ClassicAgentFixtures.SummarizePromptId }.OrderBy(id => id), cloud.PromptsOf(botId).Select(prompt => prompt.AIModelId).OrderBy(id => id));
        Assert.Equal("AgentSDK4 Flow1", cloud.Cloud.Workflows[ClassicAgentFixtures.FlowId].Name);
        Assert.Contains(created.FlowsLeftInDraft, flow => flow.WorkflowId == ClassicAgentFixtures.FlowId.ToString());
        Assert.IsType<AgentDialog>(Assert.IsType<DialogComponent>(Assert.Single(cloud.Island.ComponentsOf(botId), component => component.SchemaNameString == $"{ClassicAgentFixtures.AgentD1Schema}.agent.Agent")).Dialog);
        Assert.Equal(new Dictionary<string, string> { ["File 3.txt"] = "Policy 3", ["abc main 2.txt"] = "Main policy" }, uploads.OrderBy(upload => upload.Key, StringComparer.Ordinal).ToDictionary(upload => upload.Key, upload => upload.Value));
        Assert.Equal(Convert.ToBase64String(ClassicAgentFixtures.Icon), cloud.Island.EntityOf(botId)!.IconBase64);

        var revised = AgentWorkspaceHostPushTests.With(snapshot, "Agent D1 Copy/topics/Greeting.mcs.yml", ClassicAgentFixtures.Topic("Greeting", "Welcome back."));
        revised = AgentWorkspaceHostPushTests.With(revised, "Agent D1 Copy/topics/ThankYou.mcs.yml", null);
        revised = AgentWorkspaceHostPushTests.With(revised, "Agent D1 Copy/knowledge/files/File 3.txt", "Policy 3, revised");
        revised = AgentWorkspaceHostPushTests.With(revised, $"Agent D1 Copy/prompts/summarizedocument1-{ClassicAgentFixtures.SummarizePromptId}/prompt.json", AgentWorkspaceHostPushTests.Text(snapshot, $"Agent D1 Copy/prompts/summarizedocument1-{ClassicAgentFixtures.SummarizePromptId}/prompt.json").Replace("in one sentence", "in two sentences", StringComparison.Ordinal));
        revised = new AgentWorkspaceSnapshot(revised.Folder, revised.Files.Where(file => file.Key != "Agent D1 Copy/icon.png").Append(new KeyValuePair<string, ReadOnlyMemory<byte>>("Agent D1 Copy/icon.png", ClassicAgentFixtures.RevisedIcon)).ToDictionary(file => file.Key, file => file.Value));
        uploads.Clear();

        var redeployed = await cloud.CreateHost().PushAsync(revised, HostedCloud.Target(ClassicAgentFixtures.AgentD1Schema, await cloud.CreateHost().FindAgentAsync(HostedCloud.Target(ClassicAgentFixtures.AgentD1Schema), authentication, CancellationToken.None)), HostedCloud.Options(), authentication, CancellationToken.None);

        Assert.Equal((created.BotId, true), (redeployed.BotId, redeployed.Published));
        Assert.Single(cloud.CreatedAgents);
        Assert.Equal(cloud.Island.Inserted.Count, cloud.Island.Inserted.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(ClassicAgentFixtures.AgentD1SchemaNames.Where(name => name != $"{ClassicAgentFixtures.AgentD1Schema}.topic.ThankYou"), SchemaNamesOf(cloud, botId));
        Assert.Contains("Welcome back.", CodeSerializer.Serialize(Assert.Single(cloud.Island.ComponentsOf(botId), component => component.SchemaNameString == $"{ClassicAgentFixtures.AgentD1Schema}.topic.Greeting").RootElement!), StringComparison.Ordinal);
        Assert.Equal("Policy 3, revised", uploads["File 3.txt"]);
        Assert.Contains("in two sentences", Assert.Single(cloud.PromptsOf(botId), prompt => prompt.AIModelId == ClassicAgentFixtures.SummarizePromptId).CustomConfiguration, StringComparison.Ordinal);
        Assert.Equal(Convert.ToBase64String(ClassicAgentFixtures.RevisedIcon), cloud.Island.EntityOf(botId)!.IconBase64);
        Assert.Equal(2, cloud.PromptsOf(botId).Count);
        AssertTheDiskIsUntouched();
    }

    [Fact]
    public async Task AClassicAgentShapedLikeAgentD1_PushedUnderTheRecordsSchemaNameToANewBotAndRedeployed_NamesItsChildAgentAndVariablesAfterTheBotAndLeavesTheDiskUntouched()
    {
        const string renamedSchema = "cr468_amse2ed10406";
        var cloud = new HostedCloud();
        var uploads = KnowledgeUploads(cloud);
        var authentication = new RecordingAuthProvider();
        var snapshot = AgentWorkspaceHostPushTests.With(ClassicAgentFixtures.AgentD1("Agent D1 Copy"), $"Agent D1 Copy/environmentvariables/{ClassicAgentFixtures.AgentD1Schema}.region.mcs.yml", ClassicAgentFixtures.Variable($"{ClassicAgentFixtures.AgentD1Schema}.region", "displayName: Region\n"));
        var renamed = ClassicAgentFixtures.AgentD1SchemaNames.Select(name => renamedSchema + name.Substring(ClassicAgentFixtures.AgentD1Schema.Length)).ToArray();

        var created = await cloud.CreateHost().PushAsync(snapshot, HostedCloud.Target(renamedSchema), HostedCloud.Options(), authentication, CancellationToken.None);

        var botId = Guid.Parse(created.BotId);
        Assert.True(created.Published);
        Assert.Equal(renamed, SchemaNamesOf(cloud, botId));
        Assert.IsType<AgentDialog>(Assert.IsType<DialogComponent>(Assert.Single(cloud.Island.ComponentsOf(botId), component => component.SchemaNameString == $"{renamedSchema}.agent.Agent")).Dialog);
        Assert.Equal(new[] { $"{renamedSchema}.region" }, cloud.Island.EnvironmentVariablesInserted.Distinct(StringComparer.Ordinal));
        Assert.Equal(new[] { "File 3.txt", "abc main 2.txt" }, uploads.Keys.OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal(AuthoredSettingsPushTests.Expected(AgentWorkspaceHostPushTests.Text(snapshot, "Agent D1 Copy/settings.mcs.yml").Replace(ClassicAgentFixtures.AgentD1Schema, renamedSchema, StringComparison.Ordinal), "empty-1.0.0"), AuthoredSettingsPushTests.SettingsOf(cloud.Island.EntityOf(botId)!));

        var redeployed = await cloud.CreateHost().PushAsync(snapshot, HostedCloud.Target(renamedSchema, created.BotId), HostedCloud.Options(), authentication, CancellationToken.None);

        Assert.Equal((created.BotId, true), (redeployed.BotId, redeployed.Published));
        Assert.Single(cloud.CreatedAgents);
        Assert.Equal(cloud.Island.Inserted.Count, cloud.Island.Inserted.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(renamed, SchemaNamesOf(cloud, botId));
        Assert.DoesNotContain(cloud.Island.Inserted.Concat(cloud.Island.EnvironmentVariablesInserted), name => name.StartsWith(ClassicAgentFixtures.AgentD1Schema + ".", StringComparison.OrdinalIgnoreCase));
        AssertTheDiskIsUntouched();
    }

    [Fact]
    public async Task AClassicAgentShapedLikeAgentD1_PushedToANewBotCopilotStudioKeepsProvisioningAndPushedAgainOnceItFinishes_WritesNothingBeforeAndLeavesTheDiskUntouched()
    {
        var cloud = new HostedCloud { ProvisioningStatus = "Provisioning" };
        var authentication = new RecordingAuthProvider();
        var snapshot = ClassicAgentFixtures.AgentD1("Agent D1 Copy");

        Assert.Equal(AgentSyncDiagnosticCodes.AgentNotProvisioned, (await Assert.ThrowsAsync<AgentWorkspaceHostException>(() => cloud.CreateHost().PushAsync(snapshot, HostedCloud.Target(ClassicAgentFixtures.AgentD1Schema), HostedCloud.Options() with { ProvisioningTimeout = TimeSpan.FromSeconds(10) }, authentication, CancellationToken.None))).Diagnostics[^1].Code);
        Assert.Empty(cloud.Island.Saves);
        Assert.Empty(cloud.ContentWritesAmong(int.MaxValue));

        cloud.ProvisioningStatus = null;
        cloud.ReportProvisioningStatuses("Provisioning");
        var pushed = await cloud.CreateHost().PushAsync(snapshot, HostedCloud.Target(ClassicAgentFixtures.AgentD1Schema), HostedCloud.Options(), authentication, CancellationToken.None);

        Assert.Equal((cloud.AgentIdOf(ClassicAgentFixtures.AgentD1Schema).ToString(), true), (pushed.BotId, pushed.Published));
        Assert.Single(cloud.CreatedAgents);
        Assert.Equal(ClassicAgentFixtures.AgentD1SchemaNames, SchemaNamesOf(cloud, Guid.Parse(pushed.BotId)));
        Assert.Equal(AgentPublishServiceTests.Seconds(2, 3, 4, 1, 2, 1), cloud.Clock.Delays);
        AssertTheDiskIsUntouched();
    }

    [Fact]
    public async Task AClassicAgentAndItsCollectionShapedLikeCc1_PushedWithoutACloudCacheAndRedeployed_LandOnceTakeTheUpdatesAndLeaveTheDiskUntouched()
    {
        var cloud = new HostedCloud();
        var authentication = new RecordingAuthProvider();
        var snapshot = ClassicAgentFixtures.Cc1();

        var created = await cloud.CreateHost().PushAsync(snapshot, HostedCloud.Target(ClassicAgentFixtures.Cc1Schema), HostedCloud.Options(), authentication, CancellationToken.None);

        var botId = Guid.Parse(created.BotId);
        var collectionId = cloud.CollectionIdOf(ClassicAgentFixtures.Cc1CollectionSchema);
        Assert.True(created.Published);
        Assert.Contains(cloud.Island.LinkedCollections!(botId), collection => collection.SchemaName.Value == ClassicAgentFixtures.Cc1CollectionSchema);
        Assert.Equal(new[] { $"{ClassicAgentFixtures.Cc1Schema}.topic.ConversationStart", $"{ClassicAgentFixtures.Cc1Schema}.topic.Goodbye" }, SchemaNamesOf(cloud, collectionId));
        Assert.Contains($"{ClassicAgentFixtures.Cc1Schema}.GlobalVariableComponent.UserCountry", SchemaNamesOf(cloud, botId));
        Assert.Contains($"{ClassicAgentFixtures.Cc1Schema}.topic.Greeting", SchemaNamesOf(cloud, botId));
        Assert.Equal(Convert.ToBase64String(ClassicAgentFixtures.Icon), cloud.Island.EntityOf(botId)!.IconBase64);

        var revised = AgentWorkspaceHostPushTests.With(snapshot, $"S5Collection1/topics/{ClassicAgentFixtures.Cc1Schema}.topic.Goodbye.mcs.yml", ClassicAgentFixtures.Topic("Goodbye", "See you soon."));
        revised = AgentWorkspaceHostPushTests.With(revised, $"S5Collection1/topics/{ClassicAgentFixtures.Cc1Schema}.topic.ConversationStart.mcs.yml", null);
        revised = AgentWorkspaceHostPushTests.With(revised, "S5D2C1/topics/Greeting.mcs.yml", ClassicAgentFixtures.Topic("Greeting", "Hello again from S5."));
        var agentComponentsBefore = SchemaNamesOf(cloud, botId);

        var redeployed = await cloud.CreateHost().PushAsync(revised, HostedCloud.Target(ClassicAgentFixtures.Cc1Schema, created.BotId), HostedCloud.Options(), authentication, CancellationToken.None);

        Assert.Equal((created.BotId, true), (redeployed.BotId, redeployed.Published));
        cloud.Collections.Verify(collections => collections.CreateComponentCollectionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once());
        Assert.Equal(collectionId, cloud.CollectionIdOf(ClassicAgentFixtures.Cc1CollectionSchema));
        Assert.Equal(cloud.Island.Inserted.Count, cloud.Island.Inserted.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(new[] { $"{ClassicAgentFixtures.Cc1Schema}.topic.Goodbye" }, SchemaNamesOf(cloud, collectionId));
        Assert.Contains("See you soon.", CodeSerializer.Serialize(Assert.Single(cloud.Island.ComponentsOf(collectionId)).RootElement!), StringComparison.Ordinal);
        Assert.Equal(agentComponentsBefore, SchemaNamesOf(cloud, botId));
        Assert.Contains("Hello again from S5.", CodeSerializer.Serialize(Assert.Single(cloud.Island.ComponentsOf(botId), component => component.SchemaNameString == $"{ClassicAgentFixtures.Cc1Schema}.topic.Greeting").RootElement!), StringComparison.Ordinal);
        AssertTheDiskIsUntouched();
    }

    [Fact]
    public async Task AStandaloneWorkflowPushedToANewEnvironmentAndRedeployed_IsWrittenOnceTakesTheUpdateAndLeavesTheDiskUntouched()
    {
        var cloud = new HostedCloud();
        var authentication = new RecordingAuthProvider();
        var workflowId = Guid.NewGuid();
        var snapshot = AgentWorkspaceHostWorkflowTests.Scaffold("Weather", workflowId);
        var definitionPath = Assert.Single(snapshot.Files.Keys, path => path.EndsWith("/" + WorkflowWorkspace.DefinitionFileName, StringComparison.Ordinal));

        await cloud.CreateHost().PushWorkflowAsync(snapshot, new WorkflowSyncTarget(HostedCloud.Organization, HostedCloud.EnvironmentId, HostedCloud.TenantId, null), HostedCloud.Options(), authentication, CancellationToken.None);
        await cloud.CreateHost().PushWorkflowAsync(AgentWorkspaceHostPushTests.With(snapshot, definitionPath, AgentWorkspaceHostPushTests.Text(snapshot, definitionPath).Replace("Text returned to the caller.", "Text the flow returns.", StringComparison.Ordinal)), new WorkflowSyncTarget(HostedCloud.Organization, HostedCloud.EnvironmentId, HostedCloud.TenantId, workflowId.ToString()), HostedCloud.Options(), authentication, CancellationToken.None);

        Assert.Equal(1, cloud.StandaloneWorkflows.Calls.Count(call => call == "insert"));
        Assert.Contains("Text the flow returns.", cloud.StandaloneWorkflows.Stored(workflowId)!.ClientData, StringComparison.Ordinal);
        Assert.DoesNotContain("Text returned to the caller.", cloud.StandaloneWorkflows.Stored(workflowId)!.ClientData, StringComparison.Ordinal);
        AssertTheDiskIsUntouched();
    }

    private static string[] SchemaNamesOf(HostedCloud cloud, Guid identity) => cloud.Island.ComponentsOf(identity).Select(component => component.SchemaNameString).OrderBy(name => name, StringComparer.Ordinal).ToArray();

    private static ConcurrentDictionary<string, string> KnowledgeUploads(HostedCloud cloud)
    {
        var uploads = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        cloud.Cloud.Client.As<IStreamingKnowledgeFileClient>()
            .Setup(client => client.UploadKnowledgeFileAsync(It.IsAny<Stream>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Stream, Guid, string, CancellationToken>(async (content, _, displayName, cancellationToken) =>
            {
                using var reader = new StreamReader(content, Encoding.UTF8);
                uploads[displayName] = await reader.ReadToEndAsync(cancellationToken);
            });

        return uploads;
    }

    private static Mock<ISyncDataverseClient> CloudBackedDataverse(ConcurrentDictionary<Guid, byte[]> cloudFiles)
    {
        var dataverse = new Mock<ISyncDataverseClient>();
        var streaming = dataverse.As<IStreamingKnowledgeFileClient>();
        streaming
            .Setup(client => client.DownloadKnowledgeFileAsync(It.IsAny<Stream>(), It.IsAny<BotComponentId>(), It.IsAny<CancellationToken>()))
            .Returns<Stream, BotComponentId, CancellationToken>((destination, componentId, cancellationToken) => destination.WriteAsync(cloudFiles[componentId.Value], 0, cloudFiles[componentId.Value].Length, cancellationToken));
        streaming
            .Setup(client => client.UploadKnowledgeFileAsync(It.IsAny<Stream>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Stream, Guid, string, CancellationToken>(async (content, componentId, _, cancellationToken) =>
            {
                using var received = new MemoryStream();
                await content.CopyToAsync(received, cancellationToken);
                cloudFiles[componentId] = received.ToArray();
            });

        return dataverse;
    }

    private void AssertTheDiskIsUntouched()
    {
        var sentinel = Path.Combine(_sandbox, "sentinel");
        File.WriteAllText(sentinel, string.Empty);

        Assert.True(SpinWait.SpinUntil(() => _touched.Contains(sentinel), TimeSpan.FromSeconds(30)));
        Assert.All(_touched, path => Assert.Equal(sentinel, path));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_temporaryFolder));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_workingFolder));
    }
}
