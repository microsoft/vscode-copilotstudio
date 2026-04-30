namespace Microsoft.PowerPlatformLS.UnitTests.Impl.PullAgent
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.Agents.Platform.Content;
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.CopilotStudio.Sync;
    using Microsoft.CopilotStudio.Sync.Dataverse;
    using Microsoft.PowerPlatformLS.Contracts.FileLayout;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using Microsoft.PowerPlatformLS.Impl.PullAgent;
    using Microsoft.PowerPlatformLS.UnitTests.Impl.PullAgent.Methods;
    using Microsoft.PowerPlatformLS.UnitTests.TestUtilities;
    using Moq;
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.IO;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Xunit;
    using static Microsoft.CopilotStudio.Sync.Dataverse.SyncDataverseClient;
    using Microsoft.CopilotStudio.McsCore;
    // DirectoryPath now comes from Microsoft.CopilotStudio.McsCore

    public class AgentWriterTests
    {
        private static readonly DirectoryPath WorkspaceFolderPath = new DirectoryPath(string.Empty);
        private static readonly DirectoryPath ContractsWorkspaceFolderPath = new DirectoryPath(string.Empty);
        private static readonly AgentFilePath BotCachePath = new AgentFilePath(".mcs/botdefinition.json");
        private static readonly AgentFilePath OldBotCachePath = new AgentFilePath(".mcs/botdefinition.yml");

        [Fact]
        public async Task WriteConnections()
        {
            var filesystem = new InMemoryFileWriter();
            var islandControlPlaneServicMock = new Mock<IIslandControlPlaneService>();
            var lspLoggerMock = new Mock<ILspLogger>();
            var contentAuthoringService = islandControlPlaneServicMock.Object;
            var logger = lspLoggerMock.Object;
            var writer = new WorkspaceSynchronizer(new SyncMcsFileParser(Microsoft.CopilotStudio.McsCore.LspProjectorService.Instance), (Microsoft.CopilotStudio.McsCore.IFileAccessorFactory)filesystem, contentAuthoringService, Mock.Of<ISyncProgress>(), new Microsoft.CopilotStudio.McsCore.LspComponentPathResolver());

            AgentSyncInfo info1 = new AgentSyncInfo
            {
                AgentId = Guid.Parse("FD97085E-97A2-4AE3-A68A-CF16EB043AB7"),
                EnvironmentId = "env1",
                DataverseEndpoint = new Uri("https://contoso.com/id1"),
                SolutionVersions = new SolutionInfo { CopilotStudioSolutionVersion = Version.Parse("100.0.0") },
                AccountInfo = new AccountInfo { AccountId = "foo@bar.com", TenantId = Guid.NewGuid(), ClusterCategory = CoreServicesClusterCategory.Prod, AccountEmail = "foo" },
                AgentManagementEndpoint = new Uri("https://contoso.com/id2")
            };

            await writer.SaveSyncInfoAsync(WorkspaceFolderPath, info1);

            var info2 = await writer.GetSyncInfoAsync(WorkspaceFolderPath);

            Assert.Equal(info1.AgentId, info2.AgentId);
            Assert.Equal(info1.EnvironmentId, info2.EnvironmentId);
            Assert.Equal(info1.DataverseEndpoint, info2.DataverseEndpoint);
        }

        [Fact]
        public async Task GetSyncInfoAsyncThrowsFileNotFound()
        {
            var filesystem = new InMemoryFileWriter();
            var islandControlPlaneServicMock = new Mock<IIslandControlPlaneService>();
            var lspLoggerMock = new Mock<ILspLogger>();
            var writer = new WorkspaceSynchronizer(new SyncMcsFileParser(Microsoft.CopilotStudio.McsCore.LspProjectorService.Instance), (Microsoft.CopilotStudio.McsCore.IFileAccessorFactory)filesystem, islandControlPlaneServicMock.Object, Mock.Of<ISyncProgress>(), new Microsoft.CopilotStudio.McsCore.LspComponentPathResolver());

            var ex = await Assert.ThrowsAsync<FileNotFoundException>(() => writer.GetSyncInfoAsync(WorkspaceFolderPath));

            Assert.Contains("conn.json was not found", ex.Message);
        }

        [Fact]
        public async Task GetSyncInfoAsyncThrowsInvalidOperation()
        {
            var filesystem = new InMemoryFileWriter();
            var islandControlPlaneServicMock = new Mock<IIslandControlPlaneService>();
            var lspLoggerMock = new Mock<ILspLogger>();
            var writer = new WorkspaceSynchronizer(new SyncMcsFileParser(Microsoft.CopilotStudio.McsCore.LspProjectorService.Instance), (Microsoft.CopilotStudio.McsCore.IFileAccessorFactory)filesystem, islandControlPlaneServicMock.Object, Mock.Of<ISyncProgress>(), new Microsoft.CopilotStudio.McsCore.LspComponentPathResolver());

            var accessor = (Microsoft.CopilotStudio.McsCore.IFileAccessor)filesystem;
            using (var stream = accessor.OpenWrite(new Microsoft.CopilotStudio.McsCore.AgentFilePath(".mcs/conn.json")))
            using (var sw = new StreamWriter(stream))
            {
                await sw.WriteAsync("null");
            }

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => writer.GetSyncInfoAsync(WorkspaceFolderPath));

            Assert.Contains("Unable to process content in the connection file", ex.Message);
        }

        [Theory]
        [InlineData(true, """
kind: BotDefinition
components:
  - kind: DialogComponent
    displayName: HelloDialog
    schemaName: testbot.hello.dialog
    dialog: |
      # Name: HelloDialog
      kind: TaskDialog
      outputs:
        - propertyName: greeting
      action:
        kind: InvokeFlowTaskAction
        flowId: 12345678-1234-1234-1234-123456789abc
environmentVariables: []
flows:
  - kind: CloudFlowDefinition
    displayName: TestFlow
    isEnabled: true
    workflowId: 12345678-1234-1234-1234-123456789abc
    clientdata: "{}"
entity:
  kind: BotEntity
  version: 1
  displayName: TestBot
  schemaName: testbot
""")]
        [InlineData(false, """
{
  "$kind": "BotDefinition",
  "components": [
    {
      "$kind": "DialogComponent",
      "displayName": "HelloDialog",
      "schemaName": "testbot.hello.dialog",
      "dialog": "# Name: HelloDialog\nkind: TaskDialog\noutputs:\n  - propertyName: greeting\n\naction:\n  kind: InvokeFlowTaskAction\n  flowId: 12345678-1234-1234-1234-123456789abc\noutputMode: All"
    }
  ],
  "environmentVariables": [],
  "flows": [
    {
      "$kind": "CloudFlowDefinition",
      "displayName": "TestFlow",
      "isEnabled": true,
      "workflowId": "12345678-1234-1234-1234-123456789abc",
      "clientdata": "{}"
    }
  ],
  "entity": {
    "$kind": "BotEntity",
    "version": 1,
    "displayName": "TestBot",
    "schemaName": "testbot"
  }
}
""")]
        public async Task ReadCloudCacheSnapshotValidBotDefinition(bool useOldCache, string content)
        {
            var filesystem = new InMemoryFileWriter();
            var accessor = (Microsoft.CopilotStudio.McsCore.IFileAccessor)filesystem;

            var syncPath = useOldCache ? new Microsoft.CopilotStudio.McsCore.AgentFilePath(OldBotCachePath.ToString()) : new Microsoft.CopilotStudio.McsCore.AgentFilePath(BotCachePath.ToString());
            await accessor.WriteAsync(syncPath, content, CancellationToken.None);

            var result = WorkspaceSynchronizer.ReadCloudCacheSnapshot(accessor);

            Assert.NotNull(result);

            if (result != null)
            {
                Assert.Equal(BotElementKind.BotDefinition, result.Kind);
                Assert.Single(result.Flows);
            }
            
        }

        [Fact]
        public void ReadCloudCacheSnapshotAllowMissing()
        {
            var filesystem = new InMemoryFileWriter();
            var accessor = (Microsoft.CopilotStudio.McsCore.IFileAccessor)filesystem;

            var result = WorkspaceSynchronizer.ReadCloudCacheSnapshot(accessor, allowMissing: true);

            Assert.Null(result);
        }

        [Fact]
        public void ReadCloudCacheSnapshotThrowsFileNotFound()
        {
            var filesystem = new InMemoryFileWriter();
            var accessor = (Microsoft.CopilotStudio.McsCore.IFileAccessor)filesystem;

            var ex = Assert.Throws<FileNotFoundException>(() => WorkspaceSynchronizer.ReadCloudCacheSnapshot(accessor, allowMissing: false));

            Assert.Contains("botdefinition.json was not found", ex.Message);
        }

        // Fake OperationContext
        // Just need a non-null instance that is passed into our mock IContentAuthoringService.
        private static readonly AuthoringOperationContext FakeOperationContext = new AuthoringOperationContext(null, new CdsOrganizationInfo(), new BotReference(), null, false);

        [Fact]
        public async Task BasicCloneAsync()
        {
            var filesystem = new InMemoryFileWriter();

            CancellationToken cancel = new CancellationToken();

            string changeToken1 = "change-token-1";

            var text = TestDataReader.GetTestData("topic2.mcs.yml");
            var dialog = CodeSerializer.Deserialize<AdaptiveDialog>(text);
            var dialogComponentBuilder = new DialogComponent.Builder
            {
                SchemaName = new DialogSchemaName("cr123.topic.topic2"),
                Id = new BotComponentId(Guid.NewGuid())
            };
            var dialogComponent = dialogComponentBuilder.Build().WithDialog(dialog);

            var botComponentBuilderList = new List<BotComponentChange>
            {
                new BotComponentInsert(dialogComponent)
            };

            var botEntity = new BotEntity().WithSchemaName(new BotEntitySchemaName("cr123"));

            var changeset = new PvaComponentChangeSet(
              botComponentBuilderList,
              botEntity,
              changeToken1);

            var mock = new Mock<IIslandControlPlaneService>();
            var lspLoggerMock = new Mock<ILspLogger>();
            mock.Setup(x => x.GetComponentsAsync(FakeOperationContext, null, cancel)).Returns(Task.FromResult(changeset));

            var contentAuthoringService = mock.Object;
            var logger = lspLoggerMock.Object;

            var writer = new WorkspaceSynchronizer(new SyncMcsFileParser(Microsoft.CopilotStudio.McsCore.LspProjectorService.Instance), (Microsoft.CopilotStudio.McsCore.IFileAccessorFactory)filesystem, contentAuthoringService, Mock.Of<ISyncProgress>(), new Microsoft.CopilotStudio.McsCore.LspComponentPathResolver());

            // Test
            var referenceTracker = new ReferenceTracker();

            var dataverseClient = new MockDataverseClient();
            Guid agentId = Guid.NewGuid();
            await writer.CloneChangesAsync(WorkspaceFolderPath, referenceTracker, FakeOperationContext, dataverseClient, new AgentSyncInfo { AgentId = agentId }, cancel);

            // Validate

            var actualFilenames = filesystem.Filenames;

            Assert.Equal(
            [
               ".mcs/.gitignore",
               ".mcs/botdefinition.json",
               ".mcs/changetoken.txt",
               "agent.mcs.yml",
               "settings.mcs.yml",
               "topics/topic2.mcs.yml" // filename should have schema truncated. 
            ], actualFilenames);

            var currentChangeToken = await filesystem.ReadStringAsync(new AgentFilePath(".mcs/changetoken.txt"), cancel);
            Assert.Equal(changeToken1, currentChangeToken);

            // Empty agent.mcs.yml file written since it was missing from changeset.
            var currentAgentMcsYml = await filesystem.ReadStringAsync(new AgentFilePath("agent.mcs.yml"), cancel);
            Assert.Equal("kind: GptComponentMetadata", currentAgentMcsYml);

            // Ensure yaml gets display name and description. 
            var topicYml = await filesystem.ReadStringAsync(new AgentFilePath("topics/topic2.mcs.yml"), default);

            string expectedStart = """
kind: AdaptiveDialog
""";
            topicYml = topicYml.Replace("\r", "");
            expectedStart = expectedStart.Replace("\r", "");

            Assert.StartsWith(expectedStart, topicYml);
        }

        // Clone with a Agent.mcs.yml file
        [Fact]
        public async Task BasicCloneWithGptComponentAsync()
        {
            var filesystem = new InMemoryFileWriter();

            CancellationToken cancel = new CancellationToken();

            string changeToken1 = "change-token-1";

            var dialogComponent = new GptComponent(
                id: Guid.NewGuid(),
                displayName: "Display123",
                schemaName: ".gpt.default");

            var botComponentBuilderList = new List<BotComponentChange>
            {
                new BotComponentInsert(dialogComponent)
            };

            var botEntity = new BotEntity().WithSchemaName(new BotEntitySchemaName("cr123"));

            var changeset = new PvaComponentChangeSet(
              botComponentBuilderList,
              botEntity,
              changeToken1);

            var mock = new Mock<IIslandControlPlaneService>();
            var lspLoggerMock = new Mock<ILspLogger>();
            mock.Setup(x => x.GetComponentsAsync(FakeOperationContext, null, cancel)).Returns(Task.FromResult(changeset));

            var contentAuthoringService = mock.Object;
            var logger = lspLoggerMock.Object;

            var writer = new WorkspaceSynchronizer(new SyncMcsFileParser(Microsoft.CopilotStudio.McsCore.LspProjectorService.Instance), (Microsoft.CopilotStudio.McsCore.IFileAccessorFactory)filesystem, contentAuthoringService, Mock.Of<ISyncProgress>(), new Microsoft.CopilotStudio.McsCore.LspComponentPathResolver());

            // Test
            var referenceTracker = new ReferenceTracker();
            var dataverseClient = new MockDataverseClient();
            Guid agentId = Guid.NewGuid();
            await writer.CloneChangesAsync(WorkspaceFolderPath, referenceTracker, FakeOperationContext, dataverseClient, new AgentSyncInfo { AgentId = agentId }, cancel);

            // Validate

            var actualFilenames = filesystem.Filenames;

            Assert.Equal(
            [
               ".mcs/.gitignore",
               ".mcs/botdefinition.json",
               ".mcs/changetoken.txt",
               "agent.mcs.yml",
               "settings.mcs.yml"
            ], actualFilenames);

            var currentChangeToken = await filesystem.ReadStringAsync(new AgentFilePath(".mcs/changetoken.txt"), cancel);
            Assert.Equal(changeToken1, currentChangeToken);

            // Empty agent.mcs.yml file written since it was missing from changeset.
            var currentAgentMcsYml = await filesystem.ReadStringAsync(new AgentFilePath("agent.mcs.yml"), cancel);
            Assert.Equal("", currentAgentMcsYml.Trim());
        }


        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void KnowledgeFileSyncTest(bool hasKnowledgeFileInLocal)
        {
            var botId = Guid.NewGuid();

            var localComponent = CreateKnowledgeComponent();
            var cloudComponent = CreateKnowledgeComponent();

            var localDefinition = hasKnowledgeFileInLocal
                ? CreateDefinition(botId, localComponent)
                : CreateDefinition(botId);

            var cloudDefinition = CreateDefinition(botId, cloudComponent);

            var synchronizer = CreateSynchronizer();

            var (changeSet, changes) = synchronizer.GetLocalChanges(
                localDefinition,
                cloudDefinition,
                new InMemoryFileWriter(),
                null);

            Assert.Empty(changes);
            Assert.Empty(changeSet.BotComponentChanges);
        }

        [Theory]
        [InlineData("New Name", "Old Name", "description", "description")]                // name changed
        [InlineData("File1", "File1", "New description", "Old description")]              // description changed
        [InlineData("New Name", "Old Name", "New description", "Old description")]        // both changed
        public void KnowledgeFileSync_MetadataChanged(string localName, string cloudName, string localDescription, string cloudDescription)
        {
            var botId = Guid.NewGuid();

            var local = CreateKnowledgeComponent(
                name: localName,
                description: localDescription);

            var cloud = CreateKnowledgeComponent(
                name: cloudName,
                description: cloudDescription);

            var localDef = CreateDefinition(botId, local);
            var cloudDef = CreateDefinition(botId, cloud);

            var synchronizer = CreateSynchronizer();

            var (changeSet, changes) = synchronizer.GetLocalChanges(
                localDef,
                cloudDef,
                new InMemoryFileWriter(),
                null);

            Assert.Single(changes);
            Assert.Single(changeSet.BotComponentChanges);

            Assert.Equal(ChangeType.Update, changes[0].ChangeType);
        }

        [Fact]
        public async Task PullExistingChangesAsync_WithMergeConflict_MergesCorrectly()
        {
            string schemaName = "cr123";
            string topicSchemaName = $"{schemaName}.topic.topicWithMergeConflict";

            var cancel = new CancellationToken();
            var filesystem = new InMemoryFileWriter();
            var islandControlPlaneServiceMock = new Mock<IIslandControlPlaneService>();
            var synchronizer = new WorkspaceSynchronizer(new SyncMcsFileParser(Microsoft.CopilotStudio.McsCore.LspProjectorService.Instance), (Microsoft.CopilotStudio.McsCore.IFileAccessorFactory)filesystem, islandControlPlaneServiceMock.Object, Mock.Of<ISyncProgress>(), new Microsoft.CopilotStudio.McsCore.LspComponentPathResolver());

            // 1. Define Original, Local, and Remote versions of a component
            var componentFactory = new TestBotComponentFactory(topicSchemaName);
            var originalComponent = componentFactory.CreateDialogComponent("thanks a lot");
            var localComponent = componentFactory.CreateDialogComponent("gracias", true);
            var remoteComponent = componentFactory.CreateDialogComponent("merci!");

            // 2. Set up initial workspace state (cloud cache)
            var originalDefinition = new BotDefinition.Builder
            {
                Entity = new BotEntity().WithSchemaName(new BotEntitySchemaName(schemaName)),
                Components = { originalComponent }
            }.Build();
            WorkspaceSynchronizer.WriteCloudCache((Microsoft.CopilotStudio.McsCore.IFileAccessor)filesystem, originalDefinition);
            await ((Microsoft.CopilotStudio.McsCore.IFileAccessor)filesystem).WriteAsync(new Microsoft.CopilotStudio.McsCore.AgentFilePath(".mcs/changetoken.txt"), "original_token", cancel);

            // 3. Define the user's local view (previousDefinition)
            var previousDefinition = new BotDefinition.Builder
            {
                Entity = new BotEntity().WithSchemaName(new BotEntitySchemaName(schemaName)),
                Components = { localComponent }
            }.Build();

            // 4. Mock the remote change coming from the service
            var remoteChangeSet = new PvaComponentChangeSet(
                new List<BotComponentChange> { new BotComponentInsert(remoteComponent) },
                new BotEntity().WithSchemaName(new BotEntitySchemaName(schemaName)),
                "remote_token"
            );
            islandControlPlaneServiceMock.Setup(s => s.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), "original_token", cancel))
                .ReturnsAsync(remoteChangeSet);

            // Test pull changes
            var mergedDefinition = await synchronizer.PullExistingChangesAsync(WorkspaceFolderPath, FakeOperationContext, previousDefinition, new MockDataverseClient(), new AgentSyncInfo { AgentId = Guid.NewGuid() }, cancel);

            // Verify the merged definition contains the expected components
            Assert.Equal(
            [
               ".mcs/botdefinition.json",
               ".mcs/changetoken.txt",
               "settings.mcs.yml",
               "topics/topicWithMergeConflict.mcs.yml" // filename should have schema truncated. 
            ], filesystem.Filenames);

            // Verify the topic file contains the merged content with conflict markers
            var pathResolver = new Microsoft.CopilotStudio.McsCore.LspComponentPathResolver();
            string actualMergedContent = await filesystem.ReadStringAsync(new AgentFilePath(pathResolver.GetComponentPath(originalComponent, originalDefinition)), cancel);
            string expectedMergedContent = """
mcs.metadata:
  componentName: Thank you
  description: This topic triggers when the user says thank you.
kind: AdaptiveDialog
beginDialog:
  kind: OnRecognizedIntent
  id: main
  intent:
    displayName: Thank you
    includeInOnSelectIntent: false
    triggerQueries:
      - thanks
      - thank you
      - thanks so much
<<<<<<< 
      - gracias
=======
      - merci!
>>>>>>> 

  actions:
    - kind: SendActivity
      id: sendMessage_9iz6v7
      activity: You're welcome.
""";
            Assert.Equal(expectedMergedContent.Replace("\r\n", "\n"), actualMergedContent.Replace("\r\n", "\n"));

            // Verify the cloud cache was updated with the remote version
            var updatedCache = WorkspaceSynchronizer.ReadCloudCacheSnapshot((Microsoft.CopilotStudio.McsCore.IFileAccessor)filesystem);
            var remoteRootElement = remoteComponent?.RootElement != null ? CodeSerializer.Serialize(remoteComponent.RootElement) : null;
            string? cachedRootElement = null;
            if (updatedCache?.TryGetComponentBySchemaName(topicSchemaName, out var cachedComponent) == true)
            {
                cachedRootElement = cachedComponent?.RootElement != null ? CodeSerializer.Serialize(cachedComponent.RootElement) : null;
            };
            Assert.Equal(remoteRootElement, cachedRootElement);

            // Verify the change token was updated
            string updatedToken = await filesystem.ReadStringAsync(new AgentFilePath(".mcs/changetoken.txt"), cancel);
            Assert.Equal("remote_token", updatedToken);
        }

        [Fact]
        public async Task PullExistingChangesAsyncWithWorkflows()
        {
            var remoteWorkflowId = Guid.NewGuid();
            var remoteWorkflowName = "RemoteWorkflow";

            string schemaName = "cr123";
            string topicSchemaName = $"{schemaName}.topic.topicWithMergeConflict";
            var cancel = new CancellationToken();
            var filesystem = new InMemoryFileWriter();
            var islandControlPlaneServiceMock = new Mock<IIslandControlPlaneService>();
            var synchronizer = new WorkspaceSynchronizer(new SyncMcsFileParser(Microsoft.CopilotStudio.McsCore.LspProjectorService.Instance), (Microsoft.CopilotStudio.McsCore.IFileAccessorFactory)filesystem, islandControlPlaneServiceMock.Object, Mock.Of<ISyncProgress>(), new Microsoft.CopilotStudio.McsCore.LspComponentPathResolver());

            var componentFactory = new TestBotComponentFactory(topicSchemaName);
            var originalComponent = componentFactory.CreateDialogComponent("test dialog component");

            var workflowId = Guid.NewGuid();
            var workflowOriginal = new WorkflowMetadata
            {
                WorkflowId = workflowId,
                Name = "OriginalWorkflow",
                ClientData = @"{ ""property"": ""original-clientdata"" }"
            };
            var workflowLocal = new WorkflowMetadata
            {
                WorkflowId = workflowId,
                Name = "LocalWorkflow",
                ClientData = @"{ ""property"": ""local-clientdata"" }"
            };
            var workflowRemote = new WorkflowMetadata
            {
                WorkflowId = remoteWorkflowId,
                Name = remoteWorkflowName,
                ClientData = @"{ ""property"": ""remote-clientdata"" }"
            };

            var originalDefinition = new BotDefinition.Builder
            {
                Entity = new BotEntity().WithSchemaName(new BotEntitySchemaName(schemaName)),
                Components = { originalComponent },
                Flows = {
                    new CloudFlowDefinition(
                        displayName: workflowOriginal.Name,
                        isEnabled: true,
                        workflowId: workflowOriginal.WorkflowId,
                        extensionData: new RecordDataValue(ImmutableDictionary<string, DataValue>.Empty.Add("clientdata", DataValue.Create(workflowOriginal.ClientData)))
                    )
                }
            }.Build();

            WorkspaceSynchronizer.WriteCloudCache((Microsoft.CopilotStudio.McsCore.IFileAccessor)filesystem, originalDefinition);
            await ((Microsoft.CopilotStudio.McsCore.IFileAccessor)filesystem).WriteAsync(new Microsoft.CopilotStudio.McsCore.AgentFilePath(".mcs/changetoken.txt"), "original_token", cancel);

            var previousDefinition = new BotDefinition.Builder
            {
                Entity = new BotEntity().WithSchemaName(new BotEntitySchemaName(schemaName)),
                Components = { originalComponent },
                Flows = {
                    new CloudFlowDefinition(
                        displayName: workflowLocal.Name,
                        isEnabled: true,
                        workflowId: workflowLocal.WorkflowId,
                        extensionData: new RecordDataValue(ImmutableDictionary<string, DataValue>.Empty.Add("clientdata", DataValue.Create(workflowLocal.ClientData)))
                    )
                }
            }.Build();

            var remoteChangeSet = new PvaComponentChangeSet(
                new List<BotComponentChange> { new BotComponentInsert(originalComponent) },
                new BotEntity().WithSchemaName(new BotEntitySchemaName(schemaName)),
                "remote_token"
            );

            var dataverseClient = new MockDataverseClient();
            dataverseClient.SetWorkflowsForAgent(new[] { workflowRemote });

            islandControlPlaneServiceMock
                .Setup(s => s.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), "original_token", cancel))
                .ReturnsAsync(remoteChangeSet);

            var mergedDefinition = await synchronizer.PullExistingChangesAsync(
                WorkspaceFolderPath,
                FakeOperationContext,
                previousDefinition,
                dataverseClient,
                new AgentSyncInfo { AgentId = Guid.NewGuid() },
                cancel
            );

            Assert.Contains(mergedDefinition.Flows, f => f.WorkflowId == remoteWorkflowId && f.DisplayName == remoteWorkflowName);
        }

        [Fact]
        public async Task PushChangesetAsyncWithWorkflows()
        {
            string schemaName = "cr123";
            var cancel = new CancellationToken();
            var filesystem = new InMemoryFileWriter();
            var islandControlPlaneServiceMock = new Mock<IIslandControlPlaneService>();
            var loggerMock = Mock.Of<ILspLogger>();
            var synchronizer = new WorkspaceSynchronizer(new SyncMcsFileParser(Microsoft.CopilotStudio.McsCore.LspProjectorService.Instance), (Microsoft.CopilotStudio.McsCore.IFileAccessorFactory)filesystem, islandControlPlaneServiceMock.Object, Mock.Of<ISyncProgress>(), new Microsoft.CopilotStudio.McsCore.LspComponentPathResolver());

            var workflowId = Guid.NewGuid();
            var workflow = new CloudFlowDefinition(
                displayName: "LocalWorkflow",
                isEnabled: true,
                workflowId: workflowId,
                extensionData: new RecordDataValue(
                    ImmutableDictionary<string, DataValue>.Empty.Add(
                        "clientdata",
                        DataValue.Create(@"{ ""property"": ""local-clientdata"" }")
                    )
                )
            );

            var workflowFolder = Path.Combine(WorkspaceFolderPath.ToString(), "workflows", workflowId.ToString()).Replace("\\", "/");
            await filesystem.Create(ContractsWorkspaceFolderPath).WriteAsync(new AgentFilePath($"{workflowFolder}/workflow.json"), @"{ ""property"": ""local-clientdata"" }", cancel);
            await filesystem.Create(ContractsWorkspaceFolderPath).WriteAsync(new AgentFilePath($"{workflowFolder}/metadata.yml"), $"workflowId: {workflowId}\nname: LocalWorkflow", cancel);

            // Write minimal cloud cache to avoid FileNotFoundException
            var botDefinition = new BotDefinition.Builder
            {
                Entity = new BotEntity().WithSchemaName(new BotEntitySchemaName(schemaName)),
                Components = { },
                Flows = { workflow }
            }.Build();
            WorkspaceSynchronizer.WriteCloudCache((Microsoft.CopilotStudio.McsCore.IFileAccessor)filesystem, botDefinition);

            var agentId = Guid.NewGuid();
            var componentFactory = new TestBotComponentFactory($"{schemaName}.topic.test");
            var component = componentFactory.CreateDialogComponent("test dialog");

            var pushChangeset = new PvaComponentChangeSet(
                new List<BotComponentChange> { new BotComponentInsert(component) },
                new BotEntity().WithSchemaName(new BotEntitySchemaName(schemaName)),
                "original_token"
            );

            var dataverseClient = new MockDataverseClient();
            PvaComponentChangeSet? savedChangeSet = null;

            islandControlPlaneServiceMock
                .Setup(s => s.SaveChangesAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<PvaComponentChangeSet>(), cancel))
                .ReturnsAsync((AuthoringOperationContextBase ctx, PvaComponentChangeSet cs, CancellationToken ct) =>
                {
                    savedChangeSet = cs;
                    return cs;
                });

            await synchronizer.PushChangesetAsync(
                WorkspaceFolderPath,
                FakeOperationContext,
                pushChangeset,
                dataverseClient,
                agentId,
                null,
                cancel
            );

            var workflowJsonPath = new AgentFilePath($"{workflowFolder}/workflow.json");
            var workflowJsonContent = await filesystem.ReadStringAsync(workflowJsonPath, cancel);
            JsonDocument.Parse(workflowJsonContent);

            islandControlPlaneServiceMock.Verify(s => s.SaveChangesAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<PvaComponentChangeSet>(), cancel), Times.Once);
            Assert.NotNull(savedChangeSet);
        }


        [Fact]
        public async Task PullExistingChangesAsyncWithWorkflows_MergesUniqueConnectionReferences()
        {
            var remoteWorkflowId = Guid.NewGuid();
            var remoteWorkflowName = "RemoteWorkflow";

            string schemaName = "cr123";
            string topicSchemaName = $"{schemaName}.topic.topicWithMergeConflict";
            var cancel = new CancellationToken();
            var filesystem = new InMemoryFileWriter();
            var islandControlPlaneServiceMock = new Mock<IIslandControlPlaneService>();
            var synchronizer = new WorkspaceSynchronizer(new SyncMcsFileParser(Microsoft.CopilotStudio.McsCore.LspProjectorService.Instance), (Microsoft.CopilotStudio.McsCore.IFileAccessorFactory)filesystem, islandControlPlaneServiceMock.Object, Mock.Of<ISyncProgress>(), new Microsoft.CopilotStudio.McsCore.LspComponentPathResolver());

            var componentFactory = new TestBotComponentFactory(topicSchemaName);
            var originalComponent = componentFactory.CreateDialogComponent("test dialog component");
            var logicalName = new ConnectionReferenceLogicalName("new_sharedsendmail_9800e");
            var existingConnection1 = new ConnectionReference.Builder
            {
                ConnectionReferenceLogicalName = logicalName,
                ConnectorId = "existing_shared_sendemail"
            };

            var existingConnection2 = new ConnectionReference.Builder
            {
                ConnectionReferenceLogicalName = new ConnectionReferenceLogicalName("shared_conn"),
                ConnectorId = "existing_shared_conn"
            };

            var existingConnection3 = new ConnectionReference.Builder
            {
                ConnectionReferenceLogicalName = new ConnectionReferenceLogicalName("conn_ref"),
                ConnectorId = "existing_conn_ref"
            };

            var workflowRemote = new SyncDataverseClient.WorkflowMetadata
            {
                WorkflowId = remoteWorkflowId,
                Name = remoteWorkflowName,
                ClientData = @"
                {
                  ""properties"": {
                    ""connectionReferences"": {
                      ""shared_sendmail"": {
                        ""api"": {
                          ""name"": ""shared_sendmail""
                        },
                        ""connection"": {
                          ""connectionReferenceLogicalName"": ""new_sharedsendmail_9800e""
                        },
                        ""runtimeSource"": ""invoker""
                      }
                    },
                    ""definition"": {
                      ""$schema"": ""https://schema.management.azure.com/providers/Microsoft.Logic/schemas/2016-06-01/workflowdefinition.json#"",
                      ""contentVersion"": ""1.0.0.0"",
                      ""parameters"": {
                        ""$authentication"": {
                          ""defaultValue"": {},
                          ""type"": ""SecureObject""
                        },
                        ""$connections"": {
                          ""defaultValue"": {},
                          ""type"": ""Object""
                        }
                      },
                      ""triggers"": {
                        ""manual"": {
                          ""type"": ""Request"",
                          ""kind"": ""Skills"",
                          ""inputs"": {
                            ""schema"": {
                              ""type"": ""object"",
                              ""properties"": {
                                ""text"": {
                                  ""description"": ""Input text"",
                                  ""title"": ""Text"",
                                  ""type"": ""string"",
                                  ""x-ms-content-hint"": ""TEXT"",
                                  ""x-ms-dynamically-added"": true
                                }
                              },
                              ""required"": [
                                ""text""
                              ]
                            }
                          },
                          ""metadata"": {
                            ""operationMetadataId"": ""b8f61c18-1234-4f8a-9c5f-72fbabcdf764""
                          }
                        }
                      },
                      ""actions"": {
                        ""Respond_to_the_agent"": {
                          ""type"": ""Response"",
                          ""kind"": ""Skills"",
                          ""inputs"": {
                            ""schema"": {
                              ""type"": ""object"",
                              ""properties"": {
                                ""outtext"": {
                                  ""title"": ""OutText"",
                                  ""description"": """",
                                  ""type"": ""string"",
                                  ""x-ms-content-hint"": ""TEXT"",
                                  ""x-ms-dynamically-added"": true
                                }
                              },
                              ""additionalProperties"": {}
                            },
                            ""statusCode"": 200,
                            ""body"": {
                              ""outtext"": ""output text""
                            }
                          },
                          ""runAfter"": {
                            ""Send_an_email_notification_(V3)"": [
                              ""SUCCEEDED""
                            ]
                          },
                          ""metadata"": {
                            ""operationMetadataId"": ""81c94f73-dd52-123g-ad3b-a4686da63cc3""
                          }
                        },
                        ""Send_an_email_notification_(V3)"": {
                          ""type"": ""OpenApiConnection"",
                          ""inputs"": {
                            ""parameters"": {
                              ""request/to"": ""a@b.com"",
                              ""request/subject"": ""Test email"",
                              ""request/text"": ""\u003Cp class=\u0022editor-paragraph\u0022\u003Etest email content\u003C/p\u003E""
                            },
                            ""host"": {
                              ""apiId"": ""/providers/Microsoft.PowerApps/apis/shared_sendmail"",
                              ""operationId"": ""SendEmailV3"",
                              ""connectionName"": ""shared_sendmail""
                            }
                          },
                          ""runAfter"": {}
                        }
                      },
                      ""outputs"": {},
                      ""description"": ""When an agent calls the flow and send back a response.""
                    }
                  },
                  ""schemaVersion"": ""1.0.0.0""
                }"
            };

            var originalDefinition = new BotDefinition.Builder
            {
                Entity = new BotEntity().WithSchemaName(new BotEntitySchemaName(schemaName)),
                Components = { originalComponent },
                ConnectionReferences = { existingConnection1, existingConnection2, existingConnection2, existingConnection3, existingConnection3 }
            }.Build();

            WorkspaceSynchronizer.WriteCloudCache(filesystem, originalDefinition);
            await filesystem.WriteAsync(new AgentFilePath(".mcs/changetoken.txt"), "original_token", cancel);

            var previousDefinition = new BotDefinition.Builder
            {
                Entity = new BotEntity().WithSchemaName(new BotEntitySchemaName(schemaName)),
                Components = { originalComponent },
                ConnectionReferences = { existingConnection1, existingConnection2, existingConnection2, existingConnection3, existingConnection3 }
            }.Build();

            var remoteChangeSet = new PvaComponentChangeSet(
                new List<BotComponentChange> { new BotComponentInsert(originalComponent) },
                new BotEntity().WithSchemaName(new BotEntitySchemaName(schemaName)),
                "remote_token"
            );

            var dataverseClient = new MockDataverseClient();
            dataverseClient.SetWorkflowsForAgent(new[] { workflowRemote });
            dataverseClient.SetConnectionReferences(new[]
            {
                new SyncDataverseClient.ConnectionReferenceInfo
                {
                    ConnectionReferenceId = Guid.NewGuid(),
                    ConnectionReferenceLogicalName = "new_sharedsendmail_9800e",
                    ConnectorId = "shared_sendmail"
                }
            });

            islandControlPlaneServiceMock
                .Setup(s => s.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), "original_token", cancel))
                .ReturnsAsync(remoteChangeSet);

            var mergedDefinition = await synchronizer.PullExistingChangesAsync(
                WorkspaceFolderPath,
                FakeOperationContext,
                previousDefinition,
                dataverseClient,
                new AgentSyncInfo { AgentId = Guid.NewGuid() },
                cancel
            );

            var connections = mergedDefinition.ConnectionReferences.ToList();
            Assert.Equal(3, connections.Count);

            Assert.Contains(connections,
                c => c.ConnectionReferenceLogicalName == logicalName &&
                     c.ConnectorId == "shared_sendmail");
        }


        [Fact]
        public async Task PushChangesetAsyncWithWorkflows_MergesUniqueConnectionReferences()
        {
            string schemaName = "cr123";
            var cancel = new CancellationToken();
            var filesystem = new InMemoryFileWriter();
            var islandControlPlaneServiceMock = new Mock<IIslandControlPlaneService>();
            var loggerMock = Mock.Of<ILspLogger>();
            var synchronizer = new WorkspaceSynchronizer(new SyncMcsFileParser(Microsoft.CopilotStudio.McsCore.LspProjectorService.Instance), (Microsoft.CopilotStudio.McsCore.IFileAccessorFactory)filesystem, islandControlPlaneServiceMock.Object, Mock.Of<ISyncProgress>(), new Microsoft.CopilotStudio.McsCore.LspComponentPathResolver());

            var workflowId = Guid.NewGuid();

            var logicalName = new ConnectionReferenceLogicalName("new_sharedsendmail_9800e");

            var existingConnection1 = new ConnectionReference.Builder
            {
                ConnectionReferenceLogicalName = logicalName,
                ConnectorId = "existing_shared_sendemail"
            };

            var existingConnection2 = new ConnectionReference.Builder
            {
                ConnectionReferenceLogicalName = new ConnectionReferenceLogicalName("shared_conn"),
                ConnectorId = "existing_shared_conn"
            };

            var existingConnection3 = new ConnectionReference.Builder
            {
                ConnectionReferenceLogicalName = new ConnectionReferenceLogicalName("conn_ref"),
                ConnectorId = "existing_conn_ref"
            };

            var workflowJson = @"
            {
                ""properties"": {
                ""connectionReferences"": {
                    ""shared_sendmail"": {
                    ""api"": { ""name"": ""shared_sendmail"" },
                    ""connection"": {
                        ""connectionReferenceLogicalName"": ""new_sharedsendmail_9800e""
                    }
                    }
                }
                }
            }";

            var workflowFolder = Path.Combine(WorkspaceFolderPath.ToString(), "workflows", workflowId.ToString()).Replace("\\", "/");

            await filesystem.WriteAsync(new AgentFilePath($"{workflowFolder}/workflow.json"), workflowJson, cancel);

            await filesystem.WriteAsync(new AgentFilePath($"{workflowFolder}/metadata.yml"),
                    $"workflowId: {workflowId}\nname: LocalWorkflow", cancel);

            var botDefinition = new BotDefinition.Builder
            {
                Entity = new BotEntity().WithSchemaName(new BotEntitySchemaName(schemaName)),
                ConnectionReferences =
                {
                    existingConnection1,
                    existingConnection2,
                    existingConnection2,
                    existingConnection3,
                    existingConnection3
                }
            }.Build();

            WorkspaceSynchronizer.WriteCloudCache(filesystem, botDefinition);

            var agentId = Guid.NewGuid();

            var componentFactory = new TestBotComponentFactory($"{schemaName}.topic.test");
            var component = componentFactory.CreateDialogComponent("test dialog");

            var pushChangeset = new PvaComponentChangeSet(
                new List<BotComponentChange> { new BotComponentInsert(component) },
                new BotEntity().WithSchemaName(new BotEntitySchemaName(schemaName)),
                "original_token"
            );

            var dataverseClient = new MockDataverseClient();
            dataverseClient.SetConnectionReferences(new[]
            {
                new SyncDataverseClient.ConnectionReferenceInfo
                {
                    ConnectionReferenceId = Guid.NewGuid(),
                    ConnectionReferenceLogicalName = "new_sharedsendmail_9800e",
                    ConnectorId = "shared_sendmail"
                }
            });

            PvaComponentChangeSet? savedChangeSet = null;

            islandControlPlaneServiceMock
                .Setup(s => s.SaveChangesAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<PvaComponentChangeSet>(), cancel))
                .ReturnsAsync((AuthoringOperationContextBase ctx, PvaComponentChangeSet cs, CancellationToken ct) =>
                {
                    savedChangeSet = cs;
                    return cs;
                });

            var cloudFlowMetadata = new CloudFlowMetadata
            {
                Workflows = ImmutableArray.Create(
                    new CloudFlowDefinition(
                        displayName: "LocalWorkflow",
                        isEnabled: true,
                        workflowId: workflowId,
                        extensionData: new RecordDataValue(
                            ImmutableDictionary<string, DataValue>.Empty.Add(
                                "clientdata",
                                DataValue.Create(workflowJson)
                            )
                        )
                    )
                ),
                ConnectionReferences = ImmutableArray.Create(
                    new ConnectionReference.Builder
                    {
                        ConnectionReferenceLogicalName = logicalName,
                        ConnectorId = "shared_sendmail"
                    }.Build()
                )
            };

            await synchronizer.PushChangesetAsync(
                WorkspaceFolderPath,
                FakeOperationContext,
                pushChangeset,
                dataverseClient,
                agentId,
                cloudFlowMetadata,
                cancel
            );

            Assert.NotNull(savedChangeSet);

            var expectedFilePath = new AgentFilePath("connectionreferences.mcs.yml");
            Assert.Contains(expectedFilePath.ToString(), filesystem.Filenames);
            var connections = CodeSerializer.Deserialize<ConnectionReferencesSourceFile>(await filesystem.ReadStringAsync(expectedFilePath, cancel))?.ConnectionReferences;
            Assert.NotNull(connections);
            Assert.Equal(3, connections!.Value.Length);

            Assert.Contains(connections.Value,
                c => c.ConnectionReferenceLogicalName == logicalName &&
                     c.ConnectorId == "shared_sendmail");
        }

        [Fact]
        public async Task UpsertWorkflowForAgentAsyncTest()
        {
            using var tempWorkspace = new TempDirectory();
            var workspacePath = tempWorkspace.Path.Replace("\\", "/");
            var workspaceFolder = new DirectoryPath(workspacePath);
            var islandControlPlaneServiceMock = new Mock<IIslandControlPlaneService>();
            var synchronizer = new WorkspaceSynchronizer(new SyncMcsFileParser(Microsoft.CopilotStudio.McsCore.LspProjectorService.Instance), (Microsoft.CopilotStudio.McsCore.IFileAccessorFactory)new InMemoryFileWriter(), islandControlPlaneServiceMock.Object, Mock.Of<ISyncProgress>(), new Microsoft.CopilotStudio.McsCore.LspComponentPathResolver());

            var workflowId = Guid.NewGuid();
            var agentId = Guid.NewGuid();
            var workflowFolder = $"{workspaceFolder}/workflows/{workflowId}";
            Directory.CreateDirectory(workflowFolder.Replace("/", Path.DirectorySeparatorChar.ToString()));

            var workflowJsonPath = $"{workflowFolder}/workflow.json";
            var workflowMetadataPath = $"{workflowFolder}/metadata.yml";

            await File.WriteAllTextAsync(workflowJsonPath.Replace("/", Path.DirectorySeparatorChar.ToString()), @"{ ""property"": ""clientdata"" }");
            await File.WriteAllTextAsync(workflowMetadataPath.Replace("/", Path.DirectorySeparatorChar.ToString()), $"workflowId: {workflowId}\nname: TestWorkflow");

            var mockDataverse = new MockDataverseClient();

            await synchronizer.UpsertWorkflowForAgentAsync(workspaceFolder, mockDataverse, agentId, new CancellationToken());

            var workflowCall = mockDataverse.WorkflowCalls.SingleOrDefault();
            Assert.Equal(agentId, workflowCall.AgentId);
            Assert.Equal(workflowId, workflowCall.Metadata.WorkflowId);
        }

        [Fact]
        public async Task GetWorkflowsAsync_TestInputAndOutputTypes()
        {
            using var tempWorkspace = new TempDirectory();
            var workspaceFolder = new DirectoryPath(tempWorkspace.Path.Replace("\\", "/"));
            var workflowId = Guid.NewGuid();
            var agentId = Guid.NewGuid();

            var clientData = @"
            {
                ""properties"": {
                    ""definition"": {
                        ""triggers"": {
                            ""manual"": {
                                ""inputs"": {
                                    ""schema"": {
                                        ""properties"": {
                                            ""text"": { ""type"": ""string"", ""title"": ""Text Input"" },
                                            ""bool"": { ""type"": ""boolean"", ""title"": ""Boolean Input"" },
                                            ""number"": { ""type"": ""number"", ""title"": ""Number Input"" },
                                            ""integer"": { ""type"": ""integer"", ""title"": ""Integer Input"" },
                                            ""date"": { ""type"": ""date"", ""title"": ""Date Input"" },
                                            ""file"": { ""type"": ""object"", ""x-ms-content-hint"": ""FILE"", ""title"": ""File Input"" }
                                        }
                                    }
                                }
                            }
                        },
                        ""actions"": {
                            ""Respond_to_the_agent_test_out_1"": {
                                ""type"": ""Response"",
                                ""kind"": ""Skills"",
                                ""inputs"": {
                                    ""schema"": {
                                        ""properties"": {
                                            ""text_out"": { ""type"": ""string"", ""title"": ""Text Output"" },
                                            ""bool_out"": { ""type"": ""boolean"", ""title"": ""Boolean Output"" },
                                            ""number_out"": { ""type"": ""number"", ""title"": ""Number Output"" },
                                            ""integer_out"": { ""type"": ""integer"", ""title"": ""Integer Output"" },
                                            ""date_out"": { ""type"": ""date"", ""title"": ""Date Output"" },
                                            ""file_out"": { ""type"": ""object"", ""x-ms-content-hint"": ""FILE"", ""title"": ""File Output"" }
                                        }
                                    }
                                }
                            },
                            ""Http_Example_Action"": {
                                ""type"": ""Http"",
                                ""inputs"": {
                                    ""schema"": {
                                        ""properties"": {
                                            ""http_text"": { ""type"": ""string"", ""title"": ""http text out"" }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }";

            var mockDataverse = new MockDataverseClient();
            mockDataverse.SetWorkflowsForAgent(new[]
            {
                new WorkflowMetadata
                {
                    WorkflowId = workflowId,
                    Name = "TestWorkflow",
                    ClientData = clientData,
                    StateCode = 1
                }
            });

            var filesystem = new InMemoryFileWriter();
            var islandServiceMock = new Mock<IIslandControlPlaneService>();
            var synchronizer = new WorkspaceSynchronizer(new SyncMcsFileParser(Microsoft.CopilotStudio.McsCore.LspProjectorService.Instance), (Microsoft.CopilotStudio.McsCore.IFileAccessorFactory)filesystem, islandServiceMock.Object, Mock.Of<ISyncProgress>(), new Microsoft.CopilotStudio.McsCore.LspComponentPathResolver());

            var workflows = await synchronizer.GetWorkflowsAsync(workspaceFolder, mockDataverse, new AgentSyncInfo { AgentId = agentId }, filesystem, CancellationToken.None);

            Assert.Single(workflows.Workflows);
            var workflow = workflows.Workflows[0];

            Assert.NotNull(workflow.InputType);
            Assert.NotNull(workflow.OutputType);

            if (workflow.InputType != null && workflow.OutputType != null)
            {
                Assert.Equal(DataType.String, workflow.InputType.Properties["text"].Type);
                Assert.Equal(DataType.Boolean, workflow.InputType.Properties["bool"].Type);
                Assert.Equal(DataType.Number, workflow.InputType.Properties["number"].Type);
                Assert.Equal(DataType.Number, workflow.InputType.Properties["integer"].Type);
                Assert.Equal(DataType.DateTime, workflow.InputType.Properties["date"].Type);
                Assert.Equal(PropertyInfo.EmptyRecord.Type, workflow.InputType.Properties["file"].Type);

                Assert.Equal(DataType.String, workflow.OutputType.Properties["text_out"].Type);
                Assert.Equal(DataType.Boolean, workflow.OutputType.Properties["bool_out"].Type);
                Assert.Equal(DataType.Number, workflow.OutputType.Properties["number_out"].Type);
                Assert.Equal(DataType.Number, workflow.OutputType.Properties["integer_out"].Type);
                Assert.Equal(DataType.DateTime, workflow.OutputType.Properties["date_out"].Type);
                Assert.Equal(PropertyInfo.EmptyRecord.Type, workflow.OutputType.Properties["file_out"].Type);

                Assert.False(workflow.OutputType.Properties.ContainsKey("http_text"));
            }
        }

        [Fact]
        public async Task UpsertWorkflowForAgentAsyncWithMetadata()
        {
            using var tempWorkspace = new TempDirectory();
            var workspaceFolder = new DirectoryPath(tempWorkspace.Path.Replace("\\", "/"));
            var workflowId = Guid.NewGuid();
            var agentId = Guid.NewGuid();
            var workflowDir = Path.Combine(workspaceFolder.ToString(), "workflows", $"TestWorkflow-{workflowId}");
            Directory.CreateDirectory(workflowDir);

            await File.WriteAllTextAsync(Path.Combine(workflowDir, "workflow.json"), "{ \"test\": \"data\" }");
            await File.WriteAllTextAsync(Path.Combine(workflowDir, "metadata.yml"), $"workflowId: {workflowId}\nname: TestWorkflow");

            var mockDataverse = new MockDataverseClient();

            var synchronizer = new WorkspaceSynchronizer(
                new SyncMcsFileParser(Microsoft.CopilotStudio.McsCore.LspProjectorService.Instance),
                (Microsoft.CopilotStudio.McsCore.IFileAccessorFactory)new InMemoryFileWriter(),
                Mock.Of<IIslandControlPlaneService>(),
                Mock.Of<ISyncProgress>(),
                new Microsoft.CopilotStudio.McsCore.LspComponentPathResolver());

            var (responses, metadata) = await synchronizer.UpsertWorkflowForAgentAsync(workspaceFolder, mockDataverse, agentId, CancellationToken.None);

            Assert.Single(responses);
            Assert.Single(metadata.Workflows);
        }

        [Fact]
        public async Task UpsertWorkflowForAgentAsyncSkipsFoldersMissingFiles()
        {
            using var tempWorkspace = new TempDirectory();
            var workspaceFolder = new DirectoryPath(tempWorkspace.Path.Replace("\\", "/"));
            var workflowId = Guid.NewGuid();
            var agentId = Guid.NewGuid();
            var workflowDir = Path.Combine(workspaceFolder.ToString(), "workflows", $"TestWorkflow-{workflowId}");
            Directory.CreateDirectory(workflowDir);

            await File.WriteAllTextAsync(Path.Combine(workflowDir, "metadata.yml"), $"workflowId: {workflowId}\nname: TestWorkflow");

            var mockDataverse = new MockDataverseClient();

            var synchronizer = new WorkspaceSynchronizer(
                new SyncMcsFileParser(Microsoft.CopilotStudio.McsCore.LspProjectorService.Instance),
                (Microsoft.CopilotStudio.McsCore.IFileAccessorFactory)new InMemoryFileWriter(),
                Mock.Of<IIslandControlPlaneService>(),
                Mock.Of<ISyncProgress>(),
                new Microsoft.CopilotStudio.McsCore.LspComponentPathResolver());

            var (responses, metadata) = await synchronizer.UpsertWorkflowForAgentAsync(workspaceFolder, mockDataverse, agentId, CancellationToken.None);

            Assert.Empty(responses);
            Assert.Empty(metadata.Workflows);
        }

        [Fact]
        public async Task UpsertWorkflowForAgentAsync_NullAgentId_StillProcessesWorkflowsFromWorkspace()
        {
            using var tempWorkspace = new TempDirectory();
            var workspaceFolder = new DirectoryPath(tempWorkspace.Path.Replace("\\", "/"));
            var workflowId = Guid.NewGuid();
            var workflowDir = Path.Combine(workspaceFolder.ToString(), "workflows", $"TestWorkflow-{workflowId}");
            Directory.CreateDirectory(workflowDir);

            await File.WriteAllTextAsync(Path.Combine(workflowDir, "workflow.json"), "{ \"test\": \"data\" }");
            await File.WriteAllTextAsync(Path.Combine(workflowDir, "metadata.yml"), $"workflowId: {workflowId}\nname: TestWorkflow");

            var mockDataverse = new MockDataverseClient();

            var synchronizer = new WorkspaceSynchronizer(
                new SyncMcsFileParser(Microsoft.CopilotStudio.McsCore.LspProjectorService.Instance),
                (Microsoft.CopilotStudio.McsCore.IFileAccessorFactory)new InMemoryFileWriter(),
                Mock.Of<IIslandControlPlaneService>(),
                Mock.Of<ISyncProgress>(),
                new Microsoft.CopilotStudio.McsCore.LspComponentPathResolver());

            var (responses, metadata) = await synchronizer.UpsertWorkflowForAgentAsync(workspaceFolder, mockDataverse, agentId: null, CancellationToken.None);

            Assert.Single(responses);
            Assert.Single(metadata.Workflows);
            var call = mockDataverse.WorkflowCalls.Single();
            Assert.Null(call.AgentId);
            Assert.Equal(workflowId, call.Metadata.WorkflowId);
        }

        [Fact]
        public async Task UpsertWorkflowForAgentAsync_EmptyAgentId_StillProcessesWorkflowsFromWorkspace()
        {
            using var tempWorkspace = new TempDirectory();
            var workspaceFolder = new DirectoryPath(tempWorkspace.Path.Replace("\\", "/"));
            var workflowId = Guid.NewGuid();
            var workflowDir = Path.Combine(workspaceFolder.ToString(), "workflows", $"TestWorkflow-{workflowId}");
            Directory.CreateDirectory(workflowDir);

            await File.WriteAllTextAsync(Path.Combine(workflowDir, "workflow.json"), "{ \"test\": \"data\" }");
            await File.WriteAllTextAsync(Path.Combine(workflowDir, "metadata.yml"), $"workflowId: {workflowId}\nname: TestWorkflow");

            var mockDataverse = new MockDataverseClient();

            var synchronizer = new WorkspaceSynchronizer(
                new SyncMcsFileParser(Microsoft.CopilotStudio.McsCore.LspProjectorService.Instance),
                (Microsoft.CopilotStudio.McsCore.IFileAccessorFactory)new InMemoryFileWriter(),
                Mock.Of<IIslandControlPlaneService>(),
                Mock.Of<ISyncProgress>(),
                new Microsoft.CopilotStudio.McsCore.LspComponentPathResolver());

            var (responses, metadata) = await synchronizer.UpsertWorkflowForAgentAsync(workspaceFolder, mockDataverse, Guid.Empty, CancellationToken.None);

            Assert.Single(responses);
            Assert.Single(metadata.Workflows);
        }

        [Fact]
        public async Task PushChangesetAsync_CollectionContext_ForcesUploadAllKnowledgeFiles()
        {
            string schemaName = "cr123";
            var cancel = new CancellationToken();
            var filesystem = new InMemoryFileWriter();
            var pathResolver = new Microsoft.CopilotStudio.McsCore.LspComponentPathResolver();
            var islandControlPlaneServiceMock = new Mock<IIslandControlPlaneService>();
            var synchronizer = new WorkspaceSynchronizer(
                new SyncMcsFileParser(Microsoft.CopilotStudio.McsCore.LspProjectorService.Instance),
                (Microsoft.CopilotStudio.McsCore.IFileAccessorFactory)filesystem,
                islandControlPlaneServiceMock.Object,
                Mock.Of<ISyncProgress>(),
                pathResolver);

            var fileComponent = new FileAttachmentComponent.Builder
            {
                Id = new BotComponentId(Guid.NewGuid()),
                SchemaName = new FileAttachmentSchemaName($"{schemaName}.file.OnlyFile"),
                DisplayName = "OnlyFile.txt",
                Description = "the only file"
            }.Build();

            var botDefinition = new BotDefinition.Builder
            {
                Entity = new BotEntity().WithSchemaName(new BotEntitySchemaName(schemaName)),
                Components = { fileComponent }
            }.Build();

            WorkspaceSynchronizer.WriteCloudCache(filesystem, botDefinition);
            var filePath = new AgentFilePath(pathResolver.GetComponentPath(fileComponent, botDefinition));
            await filesystem.WriteAsync(filePath, "payload", cancel);

            var pushChangeset = new PvaComponentChangeSet(
                Array.Empty<BotComponentChange>(),
                new BotEntity().WithSchemaName(new BotEntitySchemaName(schemaName)),
                "original_token");

            islandControlPlaneServiceMock
                .Setup(s => s.SaveChangesAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<PvaComponentChangeSet>(), cancel))
                .ReturnsAsync((AuthoringOperationContextBase ctx, PvaComponentChangeSet cs, CancellationToken ct) => cs);

            var collectionContext = new BotComponentCollectionAuthoringOperationContext(
                impersonatedUser: null,
                organizationInfo: new CdsOrganizationInfo(),
                reference: new BotComponentCollectionReference(environmentId: "env1", cdsId: Guid.NewGuid()),
                solutionUniqueName: "TestSolution");

            var dataverseClient = new MockDataverseClient();

            var uploaded = await synchronizer.PushChangesetAsync(
                WorkspaceFolderPath,
                collectionContext,
                pushChangeset,
                dataverseClient,
                agentId: null,
                cloudFlowMetadata: null,
                cancel,
                uploadAllKnowledgeFiles: false);

            Assert.Equal(1, uploaded);
            Assert.Single(dataverseClient.UploadKnowledgeFileCalls);
            Assert.Equal("OnlyFile.txt", dataverseClient.UploadKnowledgeFileCalls[0].FileName);
        }

        [Fact]
        public async Task SyncWorkspaceAsync_CollectionContext_PassesFlowsAndConnectionReferences()
        {
            var cancel = CancellationToken.None;
            var workflowId = Guid.NewGuid();
            var filesystem = new InMemoryFileWriter();
            var islandControlPlaneServiceMock = new Mock<IIslandControlPlaneService>();
            var synchronizer = new WorkspaceSynchronizer(
                new SyncMcsFileParser(Microsoft.CopilotStudio.McsCore.LspProjectorService.Instance),
                (Microsoft.CopilotStudio.McsCore.IFileAccessorFactory)filesystem,
                islandControlPlaneServiceMock.Object,
                Mock.Of<ISyncProgress>(),
                new Microsoft.CopilotStudio.McsCore.LspComponentPathResolver());

            var collectionContext = new BotComponentCollectionAuthoringOperationContext(
                impersonatedUser: null,
                organizationInfo: new CdsOrganizationInfo(),
                reference: new BotComponentCollectionReference(environmentId: "env1", cdsId: Guid.NewGuid()),
                solutionUniqueName: "TestSolution");

            var emptyChangeset = new PvaComponentChangeSet(
                Array.Empty<BotComponentChange>(),
                bot: null,
                changeToken: "token1");

            islandControlPlaneServiceMock
                .Setup(s => s.GetComponentsAsync(collectionContext, null, cancel))
                .ReturnsAsync(emptyChangeset);

            var workflow = new CloudFlowDefinition(
                displayName: "RemoteFlow",
                isEnabled: true,
                workflowId: workflowId);

            var cloudFlowMetadata = new CloudFlowMetadata
            {
                Workflows = ImmutableArray.Create(workflow),
                ConnectionReferences = ImmutableArray<ConnectionReference>.Empty
            };

            var syncResult = await synchronizer.SyncWorkspaceAsync(
                WorkspaceFolderPath,
                collectionContext,
                changeToken: null,
                updateWorkspaceDirectory: false,
                new MockDataverseClient(),
                new AgentSyncInfo { ComponentCollectionId = Guid.NewGuid() },
                cloudFlowMetadata,
                cancel);

            Assert.NotNull(syncResult.Definition);
            Assert.IsType<BotComponentCollectionDefinition>(syncResult.Definition);
            Assert.Single(syncResult.Definition.Flows);
            Assert.Equal(workflowId, syncResult.Definition.Flows[0].WorkflowId.Value);
        }

        [Fact]
        public async Task GetLocalChangesAsyncWorkflowCreated()
        {
            using var tempWorkspace = new TempDirectory();
            var workspaceFolder = new DirectoryPath(tempWorkspace.Path.Replace("\\", "/"));
            var workflowId = Guid.NewGuid();
            var agentId = Guid.NewGuid();
            var cancel = CancellationToken.None;
            var workflowDir = Path.Combine(workspaceFolder.ToString(), "workflows", $"Test-{workflowId}");
            Directory.CreateDirectory(workflowDir);

            await File.WriteAllTextAsync(Path.Combine(workflowDir, "workflow.json"), "{ \"test\": \"data\" }");
            await File.WriteAllTextAsync(Path.Combine(workflowDir, "metadata.yml"), $"workflowId: {workflowId}\nname: Test");

            var botEntity = new BotEntity().WithSchemaName(new BotEntitySchemaName("cr123"));
            var emptyBotDefinition = new BotDefinition.Builder
            {
                Entity = botEntity
            }.Build();

            var filesystem = new InMemoryFileWriter();

            await ((Microsoft.CopilotStudio.McsCore.IFileAccessorFactory)filesystem).Create(workspaceFolder).WriteAsync(
                new Microsoft.CopilotStudio.McsCore.AgentFilePath(".mcs/botdefinition.json"),
                JsonSerializer.Serialize(emptyBotDefinition, new JsonSerializerOptions { WriteIndented = true }),
                cancel
            );

            await ((Microsoft.CopilotStudio.McsCore.IFileAccessorFactory)filesystem).Create(workspaceFolder).WriteAsync(new Microsoft.CopilotStudio.McsCore.AgentFilePath(".mcs/changetoken.txt"), "original_token", cancel);

            var synchronizer = new WorkspaceSynchronizer(
                new SyncMcsFileParser(Microsoft.CopilotStudio.McsCore.LspProjectorService.Instance),
                (Microsoft.CopilotStudio.McsCore.IFileAccessorFactory)filesystem,
                Mock.Of<IIslandControlPlaneService>(),
                Mock.Of<ISyncProgress>(),
                new Microsoft.CopilotStudio.McsCore.LspComponentPathResolver());

            var mockDataverse = new MockDataverseClient();
            mockDataverse.SetWorkflowsForAgent(new[]
            {
                new WorkflowMetadata
                {
                    WorkflowId = workflowId,
                    Name = "Test",
                    ClientData = "{ \"test\": \"data\" }"
                }
            });

            var (changeSet, changes) = await synchronizer.GetLocalChangesAsync(
                workspaceFolder,
                emptyBotDefinition,
                mockDataverse,
                new AgentSyncInfo { AgentId = agentId },
                cancel);

            var workflowChange = changes.Single(c => c.ChangeKind == BotElementKind.CloudFlowDefinition.ToString());

            Assert.Equal(ChangeType.Create, workflowChange.ChangeType);
            Assert.Contains(workflowId.ToString(), workflowChange.SchemaName);
        }

        [Fact]
        public async Task GetLocalChangesAsyncWorkflowUpdated()
        {
            using var tempWorkspace = new TempDirectory();
            var workspaceFolder = new DirectoryPath(tempWorkspace.Path.Replace("\\", "/"));
            var workflowId = Guid.NewGuid();
            var cancel = CancellationToken.None;
            var filesystem = new InMemoryFileWriter();
            var islandServiceMock = new Mock<IIslandControlPlaneService>();

            var synchronizer = new WorkspaceSynchronizer(
                new SyncMcsFileParser(Microsoft.CopilotStudio.McsCore.LspProjectorService.Instance),
                (Microsoft.CopilotStudio.McsCore.IFileAccessorFactory)filesystem,
                islandServiceMock.Object,
                Mock.Of<ISyncProgress>(),
                new Microsoft.CopilotStudio.McsCore.LspComponentPathResolver());

            var botEntity = new BotEntity().WithSchemaName(new BotEntitySchemaName("cr123"));

            var originalFlow = new CloudFlowDefinition(
                displayName: "Flow",
                workflowId: workflowId,
                isEnabled: true,
                extensionData: new RecordDataValue(
                    ImmutableDictionary<string, DataValue>.Empty.Add("version", DataValue.Create(1))
                )
            );

            var cloudSnapshot = new BotDefinition.Builder
            {
                Entity = botEntity,
                Flows = { originalFlow }
            }.Build();

            WorkspaceSynchronizer.WriteCloudCache(
                ((Microsoft.CopilotStudio.McsCore.IFileAccessorFactory)filesystem).Create(workspaceFolder),
                cloudSnapshot
            );

            await ((Microsoft.CopilotStudio.McsCore.IFileAccessorFactory)filesystem).Create(workspaceFolder).WriteAsync(new Microsoft.CopilotStudio.McsCore.AgentFilePath(".mcs/changetoken.txt"), "token", cancel);

            var workflowsDir = Path.Combine(workspaceFolder.ToString(), "workflows");
            Directory.CreateDirectory(workflowsDir);
            var workflowFolder = Path.Combine(workflowsDir, $"Flow-{workflowId}");
            Directory.CreateDirectory(workflowFolder);
            var updatedJson = "{ \"version\": 2 }";
            var metadataYaml = $"workflowId: {workflowId}\nname: Flow";

            await File.WriteAllTextAsync(Path.Combine(workflowFolder, "workflow.json"), updatedJson);
            await File.WriteAllTextAsync(Path.Combine(workflowFolder, "metadata.yml"), metadataYaml);

            var workspaceDefinition = new BotDefinition.Builder
            {
                Entity = botEntity
            }.Build();

            var dataverseClient = new MockDataverseClient();

            var (_, changes) = await synchronizer.GetLocalChangesAsync(
                workspaceFolder,
                workspaceDefinition,
                dataverseClient,
                new AgentSyncInfo { AgentId = Guid.NewGuid() },
                cancel
            );

            var workflowChange = changes.Single(c => c.ChangeKind == BotElementKind.CloudFlowDefinition.ToString());

            Assert.Equal(ChangeType.Update, workflowChange.ChangeType);
            Assert.Contains(workflowId.ToString(), workflowChange.SchemaName);
        }

        [Fact]
        public async Task GetLocalChangesAsyncWorkflowDeleted()
        {
            using var tempWorkspace = new TempDirectory();
            var workspaceFolder = new DirectoryPath(tempWorkspace.Path.Replace("\\", "/"));
            var cancel = CancellationToken.None;
            var workflowId = Guid.NewGuid();
            var botEntity = new BotEntity().WithSchemaName(new BotEntitySchemaName("cr123"));

            var originalFlow = new CloudFlowDefinition(
                displayName: "FlowToDelete",
                workflowId: workflowId,
                isEnabled: true,
                extensionData: new RecordDataValue(ImmutableDictionary<string, DataValue>.Empty.Add("version", DataValue.Create(1)))
            );

            var originalDefinition = new BotDefinition.Builder
            {
                Entity = botEntity,
                Flows = { originalFlow }
            }.Build();

            var filesystem = new InMemoryFileWriter();

            WorkspaceSynchronizer.WriteCloudCache((Microsoft.CopilotStudio.McsCore.IFileAccessor)filesystem, originalDefinition);
            await ((Microsoft.CopilotStudio.McsCore.IFileAccessorFactory)filesystem).Create(workspaceFolder)
                .WriteAsync(new Microsoft.CopilotStudio.McsCore.AgentFilePath(".mcs/changetoken.txt"), "token", cancel);

            var synchronizer = new WorkspaceSynchronizer(
                new SyncMcsFileParser(Microsoft.CopilotStudio.McsCore.LspProjectorService.Instance),
                (Microsoft.CopilotStudio.McsCore.IFileAccessorFactory)filesystem,
                Mock.Of<IIslandControlPlaneService>(),
                Mock.Of<ISyncProgress>(),
                new Microsoft.CopilotStudio.McsCore.LspComponentPathResolver());

            var dataverseClient = new MockDataverseClient();

            var (_, changes) = await synchronizer.GetLocalChangesAsync(
                workspaceFolder,
                new BotDefinition.Builder { Entity = botEntity }.Build(),
                dataverseClient,
                new AgentSyncInfo { AgentId = Guid.NewGuid() },
                cancel);

            var workflowChange = changes.Single(c => c.ChangeKind == BotElementKind.CloudFlowDefinition.ToString());

            Assert.Equal(ChangeType.Delete, workflowChange.ChangeType);
            Assert.Contains(workflowId.ToString(), workflowChange.SchemaName);
        }
        [Fact]
        public async Task GetRemoteChangesAsyncWorkflowCreated()
        {
            using var tempWorkspace = new TempDirectory();
            var workspaceFolder = new DirectoryPath(tempWorkspace.Path.Replace("\\", "/"));
            var cancel = CancellationToken.None;
            var botEntity = new BotEntity().WithSchemaName(new BotEntitySchemaName("cr123"));
            var emptyDefinition = new BotDefinition.Builder { Entity = botEntity }.Build();
            var filesystem = new InMemoryFileWriter();
            WorkspaceSynchronizer.WriteCloudCache((Microsoft.CopilotStudio.McsCore.IFileAccessor)filesystem, emptyDefinition);
            await ((Microsoft.CopilotStudio.McsCore.IFileAccessorFactory)filesystem).Create(workspaceFolder).WriteAsync(new Microsoft.CopilotStudio.McsCore.AgentFilePath(".mcs/changetoken.txt"), "", cancel);

            var islandMock = new Mock<IIslandControlPlaneService>();
            islandMock
                .Setup(x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), cancel))
                .ReturnsAsync(new PvaComponentChangeSet(new List<BotComponentChange>(), botEntity, "token"));

            var workflowId = Guid.NewGuid();
            var dataverseClient = new MockDataverseClient();
            dataverseClient.SetWorkflowsForAgent(new[]
            {
                new WorkflowMetadata
                {
                    WorkflowId = workflowId,
                    Name = "RemoteWorkflow",
                    ClientData = "{ \"test\": true }"
                }
            });

            var synchronizer = new WorkspaceSynchronizer(
                new SyncMcsFileParser(Microsoft.CopilotStudio.McsCore.LspProjectorService.Instance),
                (Microsoft.CopilotStudio.McsCore.IFileAccessorFactory)filesystem,
                islandMock.Object,
                Mock.Of<ISyncProgress>(),
                new Microsoft.CopilotStudio.McsCore.LspComponentPathResolver());

            var (_, changes) = await synchronizer.GetRemoteChangesAsync(
                workspaceFolder,
                FakeOperationContext,
                dataverseClient,
                new AgentSyncInfo { AgentId = Guid.NewGuid() },
                cancel);

            var workflowChange = changes.Single(c => c.ChangeKind == BotElementKind.CloudFlowDefinition.ToString());
            Assert.Equal(ChangeType.Create, workflowChange.ChangeType);
            Assert.Contains(workflowId.ToString(), workflowChange.SchemaName);
        }

        [Fact]
        public async Task GetRemoteChangesAsyncWorkflowUpdated()
        {
            using var tempWorkspace = new TempDirectory();
            var workspaceFolder = new DirectoryPath(tempWorkspace.Path.Replace("\\", "/"));
            var cancel = CancellationToken.None;
            var workflowId = Guid.NewGuid();
            var botEntity = new BotEntity().WithSchemaName(new BotEntitySchemaName("cr123"));

            var originalFlow = new CloudFlowDefinition(
                displayName: "Flow",
                workflowId: workflowId,
                isEnabled: true,
                extensionData: new RecordDataValue(ImmutableDictionary<string, DataValue>.Empty.Add("version", DataValue.Create(1)))
            );

            var originalDefinition = new BotDefinition.Builder
            {
                Entity = botEntity,
                Flows = { originalFlow }
            }.Build();

            var filesystem = new InMemoryFileWriter();
            WorkspaceSynchronizer.WriteCloudCache((Microsoft.CopilotStudio.McsCore.IFileAccessor)filesystem, originalDefinition);
            await ((Microsoft.CopilotStudio.McsCore.IFileAccessorFactory)filesystem).Create(workspaceFolder).WriteAsync(new Microsoft.CopilotStudio.McsCore.AgentFilePath(".mcs/changetoken.txt"), "token", cancel);

            var islandMock = new Mock<IIslandControlPlaneService>();
            islandMock
                .Setup(x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), cancel))
                .ReturnsAsync(new PvaComponentChangeSet(new List<BotComponentChange>(), botEntity, "token"));

            var dataverseClient = new MockDataverseClient();
            dataverseClient.SetWorkflowsForAgent(new[]
            {
                new WorkflowMetadata
                {
                    WorkflowId = workflowId,
                    Name = "Flow",
                    ClientData = "{ \"version\": 2 }"
                }
            });

            var synchronizer = new WorkspaceSynchronizer(
                new SyncMcsFileParser(Microsoft.CopilotStudio.McsCore.LspProjectorService.Instance),
                (Microsoft.CopilotStudio.McsCore.IFileAccessorFactory)filesystem,
                islandMock.Object,
                Mock.Of<ISyncProgress>(),
                new Microsoft.CopilotStudio.McsCore.LspComponentPathResolver());

            var (_, changes) = await synchronizer.GetRemoteChangesAsync(
                workspaceFolder,
                FakeOperationContext,
                dataverseClient,
                new AgentSyncInfo { AgentId = Guid.NewGuid() },
                cancel);

            var workflowChange = changes.Single(c => c.ChangeKind == BotElementKind.CloudFlowDefinition.ToString());
            Assert.Equal(ChangeType.Update, workflowChange.ChangeType);
            Assert.Contains(workflowId.ToString(), workflowChange.SchemaName);
        }

        [Fact]
        public async Task GetRemoteChangesAsyncWorkflowDeleted()
        {
            using var tempWorkspace = new TempDirectory();
            var workspaceFolder = new DirectoryPath(tempWorkspace.Path.Replace("\\", "/"));
            var cancel = CancellationToken.None;
            var workflowId = Guid.NewGuid();
            var botEntity = new BotEntity().WithSchemaName(new BotEntitySchemaName("cr123"));

            var originalFlow = new CloudFlowDefinition(
                displayName: "FlowToDelete",
                workflowId: workflowId,
                isEnabled: true,
                extensionData: new RecordDataValue(ImmutableDictionary<string, DataValue>.Empty.Add("version", DataValue.Create(1)))
            );

            var originalDefinition = new BotDefinition.Builder
            {
                Entity = botEntity,
                Flows = { originalFlow }
            }.Build();

            var filesystem = new InMemoryFileWriter();
            WorkspaceSynchronizer.WriteCloudCache((Microsoft.CopilotStudio.McsCore.IFileAccessor)filesystem, originalDefinition);
            await ((Microsoft.CopilotStudio.McsCore.IFileAccessorFactory)filesystem).Create(workspaceFolder).WriteAsync(new Microsoft.CopilotStudio.McsCore.AgentFilePath(".mcs/changetoken.txt"), "token", cancel);

            var islandMock = new Mock<IIslandControlPlaneService>();
            islandMock
                .Setup(x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), cancel))
                .ReturnsAsync(new PvaComponentChangeSet(new List<BotComponentChange>(), botEntity, "token"));

            var dataverseClient = new MockDataverseClient();
            dataverseClient.SetWorkflowsForAgent(Array.Empty<WorkflowMetadata>());

            var synchronizer = new WorkspaceSynchronizer(
                new SyncMcsFileParser(Microsoft.CopilotStudio.McsCore.LspProjectorService.Instance),
                (Microsoft.CopilotStudio.McsCore.IFileAccessorFactory)filesystem,
                islandMock.Object,
                Mock.Of<ISyncProgress>(),
                new Microsoft.CopilotStudio.McsCore.LspComponentPathResolver());

            var (_, changes) = await synchronizer.GetRemoteChangesAsync(
                workspaceFolder,
                FakeOperationContext,
                dataverseClient,
                new AgentSyncInfo { AgentId = Guid.NewGuid() },
                cancel);

            var workflowChange = changes.Single(c => c.ChangeKind == BotElementKind.CloudFlowDefinition.ToString());
            Assert.Equal(ChangeType.Delete, workflowChange.ChangeType);
            Assert.Contains(workflowId.ToString(), workflowChange.SchemaName);
        }

        [Fact]
        public async Task GetWorkflowsAsyncRemoteEmptyClearsWorkspaceAndCache()
        {
            using var tempWorkspace = new TempDirectory();
            var workspaceFolder = new DirectoryPath(tempWorkspace.Path.Replace("\\", "/"));
            var workflowId = Guid.NewGuid();
            var agentId = Guid.NewGuid();

            var workflowsRoot = Path.Combine(workspaceFolder.ToString(), "workflows");
            var workflowFolder = Path.Combine(workflowsRoot, $"Test-{workflowId}");

            Directory.CreateDirectory(workflowFolder);
            await File.WriteAllTextAsync(Path.Combine(workflowFolder, "workflow.json"), "{ \"test\": true }");
            await File.WriteAllTextAsync(Path.Combine(workflowFolder, "metadata.yml"), $"workflowId: {workflowId}\nname: Test");

            var mockDataverse = new MockDataverseClient();
            mockDataverse.SetWorkflowsForAgent(Array.Empty<WorkflowMetadata>());

            var filesystem = new InMemoryFileWriter();

            var synchronizer = new WorkspaceSynchronizer(
                new SyncMcsFileParser(Microsoft.CopilotStudio.McsCore.LspProjectorService.Instance),
                (Microsoft.CopilotStudio.McsCore.IFileAccessorFactory)filesystem,
                Mock.Of<IIslandControlPlaneService>(),
                Mock.Of<ISyncProgress>(),
                new Microsoft.CopilotStudio.McsCore.LspComponentPathResolver());

            var metadata = await synchronizer.GetWorkflowsAsync(
                workspaceFolder,
                mockDataverse,
                new AgentSyncInfo { AgentId = agentId },
                filesystem,
                CancellationToken.None);

            Assert.Empty(metadata.Workflows);
            Assert.False(Directory.Exists(workflowFolder));
        }

        [Fact]
        public async Task ProvisionConnectionReferencesAsync_ProvisionsConnections()
        {
            var islandControlPlaneServiceMock = new Mock<IIslandControlPlaneService>();
            var synchronizer = new WorkspaceSynchronizer(new SyncMcsFileParser(Microsoft.CopilotStudio.McsCore.LspProjectorService.Instance), (Microsoft.CopilotStudio.McsCore.IFileAccessorFactory)new InMemoryFileWriter(), islandControlPlaneServiceMock.Object, Mock.Of<ISyncProgress>(), new Microsoft.CopilotStudio.McsCore.LspComponentPathResolver());

            var connectionReferences = new List<ConnectionReference>
            {
                new ConnectionReference.Builder
                {
                    ConnectionReferenceLogicalName = new ConnectionReferenceLogicalName("cr123_sharedmsnweather_12345"),
                    ConnectorId = new ConnectorId("/providers/Microsoft.PowerApps/apis/shared_msnweather")
                }.Build(),
                new ConnectionReference.Builder
                {
                    ConnectionReferenceLogicalName = new ConnectionReferenceLogicalName("cr123_sharedsendmail_67890"),
                    ConnectorId = new ConnectorId("/providers/Microsoft.PowerApps/apis/shared_sendmail")
                }.Build()
            };

            var definition = new BotDefinition.Builder
            {
                ConnectionReferences = { connectionReferences[0], connectionReferences[1] }
            }.Build();

            var mockDataverse = new MockDataverseClientWithConnectionTracking();

            await synchronizer.ProvisionConnectionReferencesAsync(definition, mockDataverse, new CancellationToken());

            Assert.Equal(2, mockDataverse.ProvisionedConnections.Count);
            Assert.Contains(mockDataverse.ProvisionedConnections, c => c.name == "cr123_sharedmsnweather_12345" && c.connectorId == "/providers/Microsoft.PowerApps/apis/shared_msnweather");
            Assert.Contains(mockDataverse.ProvisionedConnections, c => c.name == "cr123_sharedsendmail_67890" && c.connectorId == "/providers/Microsoft.PowerApps/apis/shared_sendmail");
        }

        [Fact]
        public async Task ProvisionConnectionReferencesAsync_WithNonBotDefinition_DoesNothing()
        {
            var islandControlPlaneServiceMock = new Mock<IIslandControlPlaneService>();
            var synchronizer = new WorkspaceSynchronizer(new SyncMcsFileParser(Microsoft.CopilotStudio.McsCore.LspProjectorService.Instance), (Microsoft.CopilotStudio.McsCore.IFileAccessorFactory)new InMemoryFileWriter(), islandControlPlaneServiceMock.Object, Mock.Of<ISyncProgress>(), new Microsoft.CopilotStudio.McsCore.LspComponentPathResolver());

            var definition = new BotComponentCollectionDefinition();
            var mockDataverse = new MockDataverseClientWithConnectionTracking();

            await synchronizer.ProvisionConnectionReferencesAsync(definition, mockDataverse, new CancellationToken());

            Assert.Empty(mockDataverse.ProvisionedConnections);
        }

        [Fact]
        public async Task ProvisionConnectionReferencesAsync_WithNoConnections_DoesNothing()
        {
            var islandControlPlaneServiceMock = new Mock<IIslandControlPlaneService>();
            var synchronizer = new WorkspaceSynchronizer(new SyncMcsFileParser(Microsoft.CopilotStudio.McsCore.LspProjectorService.Instance), (Microsoft.CopilotStudio.McsCore.IFileAccessorFactory)new InMemoryFileWriter(), islandControlPlaneServiceMock.Object, Mock.Of<ISyncProgress>(), new Microsoft.CopilotStudio.McsCore.LspComponentPathResolver());

            var botEntity = new BotEntity();
            var definition = new BotDefinition.Builder
            {
                Entity = botEntity
            }.Build();

            var mockDataverse = new MockDataverseClientWithConnectionTracking();

            await synchronizer.ProvisionConnectionReferencesAsync(definition, mockDataverse, new CancellationToken());

            Assert.Empty(mockDataverse.ProvisionedConnections);
        }


        [Fact]
        public async Task CloneEnvironmentVariableAsync()
        {
            var filesystem = new InMemoryFileWriter();
            var cancel = CancellationToken.None;
            var dataverseClient = new MockDataverseClient();
            var referenceTracker = new ReferenceTracker();
            var lspLoggerMock = new Mock<ILspLogger>();

            var envVar = new EnvironmentVariableDefinition(
                id: new EnvironmentVariableDefinitionId(Guid.NewGuid()),
                schemaName: new EnvironmentVariableDefinitionSchemaName("cr123.cloneTestEnvVar"),
                displayName: "CloneTestEnvVar",
                defaultValue: "initial value",
                valueComponent: new EnvironmentVariableValue(value: "initial value")
            );

            var botEntity = new BotEntity().WithSchemaName(new BotEntitySchemaName("cr123"));
            var localBotDefinition = new BotDefinition.Builder
            {
                Entity = botEntity,
                EnvironmentVariables = { envVar }
            }.Build();

            var environmentVariableChanges = new List<EnvironmentVariableChange>
            {
                new EnvironmentVariableInsert(envVar)
            };

            var changeset = new PvaComponentChangeSet(
                  null,
                  null,
                  environmentVariableChanges,
                  null,
                  null,
                  null,
                  null,
                  null,
                  null,
                  botEntity,
                  "envvar-token");

            var contentAuthoringServiceMock = new Mock<IIslandControlPlaneService>();
            contentAuthoringServiceMock
                .Setup(x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContext>(), null, cancel))
                .ReturnsAsync(changeset);

            var synchronizer = new WorkspaceSynchronizer(new SyncMcsFileParser(Microsoft.CopilotStudio.McsCore.LspProjectorService.Instance), (Microsoft.CopilotStudio.McsCore.IFileAccessorFactory)filesystem, contentAuthoringServiceMock.Object, Mock.Of<ISyncProgress>(), new Microsoft.CopilotStudio.McsCore.LspComponentPathResolver());

            Guid agentId = Guid.NewGuid();
            await synchronizer.CloneChangesAsync(WorkspaceFolderPath, referenceTracker, FakeOperationContext, dataverseClient, new AgentSyncInfo { AgentId = agentId }, cancel);

            var expectedFilePath = new AgentFilePath("environmentvariables/cr123.cloneTestEnvVar.mcs.yml");
            Assert.Contains(expectedFilePath.ToString(), filesystem.Filenames);

            var content = await filesystem.ReadStringAsync(expectedFilePath, cancel);
            Assert.Contains("CloneTestEnvVar", content);
            Assert.Contains("initial value", content);
        }

        [Fact]
        public async Task PullEnvironmentVariableChangesAsync()
        {
            string schemaName = "cr123";
            string topicSchemaName = $"{schemaName}.topic.topicWithMergeConflict";
            var cancel = new CancellationToken();
            var filesystem = new InMemoryFileWriter();
            var islandControlPlaneServiceMock = new Mock<IIslandControlPlaneService>();
            var synchronizer = new WorkspaceSynchronizer(new SyncMcsFileParser(Microsoft.CopilotStudio.McsCore.LspProjectorService.Instance), (Microsoft.CopilotStudio.McsCore.IFileAccessorFactory)filesystem, islandControlPlaneServiceMock.Object, Mock.Of<ISyncProgress>(), new Microsoft.CopilotStudio.McsCore.LspComponentPathResolver());

            var componentFactory = new TestBotComponentFactory(topicSchemaName);
            var originalComponent = componentFactory.CreateDialogComponent("test dialog component");

            var cloudEnvVarUpdate = new EnvironmentVariableDefinition(
                id: new EnvironmentVariableDefinitionId(Guid.NewGuid()),
                schemaName: new EnvironmentVariableDefinitionSchemaName($"{schemaName}.envUpdate"),
                displayName: "EnvUpdate",
                defaultValue: "cloudValue",
                valueComponent: new EnvironmentVariableValue(value: "cloudValue")
            );

            var cloudEnvVarDelete = new EnvironmentVariableDefinition(
                id: new EnvironmentVariableDefinitionId(Guid.NewGuid()),
                schemaName: new EnvironmentVariableDefinitionSchemaName($"{schemaName}.envDelete"),
                displayName: "EnvDelete",
                defaultValue: "deleteValue",
                valueComponent: new EnvironmentVariableValue(value: "deleteValue")
            );

            var originalDefinition = new BotDefinition.Builder
            {
                Entity = new BotEntity().WithSchemaName(new BotEntitySchemaName(schemaName)),
                Components = { originalComponent },
                EnvironmentVariables = { cloudEnvVarUpdate, cloudEnvVarDelete }
            }.Build();

            WorkspaceSynchronizer.WriteCloudCache(filesystem, originalDefinition);
            await filesystem.WriteAsync(new AgentFilePath(".mcs/changetoken.txt"), "original_token", cancel);

            var previousDefinition = new BotDefinition.Builder
            {
                Entity = new BotEntity().WithSchemaName(new BotEntitySchemaName(schemaName)),
                Components = { originalComponent },
                EnvironmentVariables = { new EnvironmentVariableDefinition(
                    id: cloudEnvVarUpdate.Id,
                    schemaName: cloudEnvVarUpdate.SchemaName,
                    displayName: "EnvUpdate",
                    defaultValue: "cloudValue",
                    valueComponent: new EnvironmentVariableValue(value: "updatedValue")
                )}
            }.Build();

            var environmentVariableChanges = new List<EnvironmentVariableChange>
            {
                new EnvironmentVariableUpdate(cloudEnvVarUpdate),
                new EnvironmentVariableDelete(cloudEnvVarDelete.Id, cloudEnvVarDelete.ValueComponent!.Id, cloudEnvVarDelete.Version)
            };

            var remoteChangeSet = new PvaComponentChangeSet(
                 null,
                 null,
                 environmentVariableChanges,
                 null,
                 null,
                 null,
                 null,
                 null,
                 null,
                 new BotEntity().WithSchemaName(new BotEntitySchemaName(schemaName)),
                 "remote_token");

            islandControlPlaneServiceMock
                .Setup(s => s.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), "original_token", cancel))
                .ReturnsAsync(remoteChangeSet);

            var pulledDefinition = await synchronizer.PullExistingChangesAsync(
                WorkspaceFolderPath,
                FakeOperationContext,
                previousDefinition,
                new MockDataverseClient(),
                new AgentSyncInfo { AgentId = Guid.NewGuid() },
                cancel
            );

            Assert.NotNull(pulledDefinition);
            var pulledEnvVars = pulledDefinition.EnvironmentVariables.ToList();
            Assert.Contains(pulledEnvVars, ev => ev.SchemaName.Value == $"{schemaName}.envUpdate");
            Assert.DoesNotContain(pulledEnvVars, ev => ev.SchemaName.Value == $"{schemaName}.envDelete");
        }

        [Fact]
        public async Task PushEnvironmentVariableChangesetAsync()
        {
            string schemaName = "cr123";
            string topicSchemaName = $"{schemaName}.topic.topicWithMergeConflict";
            var cancel = new CancellationToken();
            var filesystem = new InMemoryFileWriter();
            var islandControlPlaneServiceMock = new Mock<IIslandControlPlaneService>();

            var componentFactory = new TestBotComponentFactory(topicSchemaName);
            var originalComponent = componentFactory.CreateDialogComponent("test dialog component");

            var cloudEnvVarUpdate = new EnvironmentVariableDefinition(
                id: new EnvironmentVariableDefinitionId(Guid.NewGuid()),
                schemaName: new EnvironmentVariableDefinitionSchemaName($"{schemaName}.envUpdate"),
                displayName: "EnvUpdate",
                defaultValue: "cloudValue",
                valueComponent: new EnvironmentVariableValue(value: "cloudValue")
            );

            var cloudEnvVarDelete = new EnvironmentVariableDefinition(
                id: new EnvironmentVariableDefinitionId(Guid.NewGuid()),
                schemaName: new EnvironmentVariableDefinitionSchemaName($"{schemaName}.envDelete"),
                displayName: "EnvDelete",
                defaultValue: "deleteValue",
                valueComponent: new EnvironmentVariableValue(value: "deleteValue")
            );

            var localNewEnvVar = new EnvironmentVariableDefinition(
                id: new EnvironmentVariableDefinitionId(Guid.NewGuid()),
                schemaName: new EnvironmentVariableDefinitionSchemaName($"{schemaName}.envNew"),
                displayName: "EnvNew",
                defaultValue: "newValue",
                valueComponent: new EnvironmentVariableValue(value: "newValue")
            );

            var environmentVariableChanges = new List<EnvironmentVariableChange>
            {
                new EnvironmentVariableUpdate(cloudEnvVarUpdate),
                new EnvironmentVariableDelete(cloudEnvVarDelete.Id, cloudEnvVarDelete.ValueComponent!.Id, cloudEnvVarDelete.Version),
                new EnvironmentVariableInsert(localNewEnvVar)
            };

            var originalDefinition = new BotDefinition.Builder
            {
                Entity = new BotEntity().WithSchemaName(new BotEntitySchemaName(schemaName)),
                Components = { originalComponent },
                EnvironmentVariables = { cloudEnvVarUpdate, cloudEnvVarDelete }
            }.Build();

            WorkspaceSynchronizer.WriteCloudCache(filesystem, originalDefinition);
            await filesystem.WriteAsync(new AgentFilePath(".mcs/changetoken.txt"), "original_token", cancel);

            var pushChangeset = new PvaComponentChangeSet(
                null,
                null,
                environmentVariableChanges,
                null,
                null,
                null,
                null,
                null,
                null,
                new BotEntity().WithSchemaName(new BotEntitySchemaName(schemaName)),
                "remote_token");

            PvaComponentChangeSet? savedChangeSet = null;

            islandControlPlaneServiceMock
                .Setup(s => s.SaveChangesAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<PvaComponentChangeSet>(), cancel))
                .ReturnsAsync((AuthoringOperationContextBase ctx, PvaComponentChangeSet cs, CancellationToken ct) =>
                {
                    savedChangeSet = cs;
                    return cs;
                });

            var synchronizer = new WorkspaceSynchronizer(new SyncMcsFileParser(Microsoft.CopilotStudio.McsCore.LspProjectorService.Instance), (Microsoft.CopilotStudio.McsCore.IFileAccessorFactory)filesystem, islandControlPlaneServiceMock.Object, Mock.Of<ISyncProgress>(), new Microsoft.CopilotStudio.McsCore.LspComponentPathResolver());

            await synchronizer.PushChangesetAsync(
                WorkspaceFolderPath,
                FakeOperationContext,
                pushChangeset,
                new MockDataverseClient(),
                Guid.NewGuid(),
                cloudFlowMetadata: null,
                cancel
            );

            islandControlPlaneServiceMock.Verify(s => s.SaveChangesAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<PvaComponentChangeSet>(), cancel), Times.Once);
            Assert.NotNull(savedChangeSet);

            var pushedEnvVars = pushChangeset.EnvironmentVariableChanges.ToList();

            Assert.Contains(pushedEnvVars, ev => ev is EnvironmentVariableUpdate u && u.EnvironmentVariable?.SchemaName.Value == $"{schemaName}.envUpdate");
            Assert.Contains(pushedEnvVars, ev => ev is EnvironmentVariableInsert u && u.EnvironmentVariable?.SchemaName.Value == $"{schemaName}.envNew");
            Assert.Contains(pushedEnvVars, ev => ev is EnvironmentVariableDelete d && d.DefinitionId.Value == cloudEnvVarDelete.Id);
        }

        [Fact]
        public async Task EnvironmentVariable_LocalCreate_WhenFileExists()
        {
            var schemaName = "cr123";

            var env = new EnvironmentVariableDefinition(
                id: new EnvironmentVariableDefinitionId(Guid.NewGuid()),
                schemaName: new EnvironmentVariableDefinitionSchemaName($"{schemaName}.env1"),
                displayName: "env1",
                defaultValue: "value1",
                valueComponent: null,
                version: 1
            );

            var localDefinition = new BotDefinition.Builder
            {
                Entity = new BotEntity().WithSchemaName(new BotEntitySchemaName(schemaName)),
                EnvironmentVariables = { env }
            }.Build();

            var cloudDefinition = new BotDefinition.Builder
            {
                Entity = new BotEntity().WithSchemaName(new BotEntitySchemaName(schemaName)),
            }.Build();

            var fileAccessor = new InMemoryFileWriter();
            await fileAccessor.WriteAsync(new AgentFilePath($"environmentvariables/{env.SchemaName.Value}.mcs.yml"), "content", CancellationToken.None);

            var synchronizer = CreateSynchronizer();

            var (changeSet, changes) = synchronizer.GetLocalChanges(localDefinition, cloudDefinition, fileAccessor, null);

            Assert.Single(changes);
            Assert.Equal(ChangeType.Create, changes[0].ChangeType);
            Assert.Single(changeSet.EnvironmentVariableChanges);
            Assert.IsType<EnvironmentVariableInsert>(changeSet.EnvironmentVariableChanges[0]);
        }

        [Fact]
        public void EnvironmentVariable_NoCreate_WhenFileMissing()
        {
            var schemaName = "cr123";

            var env = new EnvironmentVariableDefinition(
                id: new EnvironmentVariableDefinitionId(Guid.NewGuid()),
                schemaName: new EnvironmentVariableDefinitionSchemaName($"{schemaName}.env1"),
                displayName: "env1",
                defaultValue: "value1",
                valueComponent: null,
                version: 1
            );

            var localDefinition = new BotDefinition.Builder
            {
                Entity = new BotEntity().WithSchemaName(new BotEntitySchemaName(schemaName)),
                EnvironmentVariables = { env }
            }.Build();

            var cloudDefinition = new BotDefinition.Builder
            {
                Entity = new BotEntity().WithSchemaName(new BotEntitySchemaName(schemaName)),
            }.Build();

            var synchronizer = CreateSynchronizer();

            var (changeSet, changes) = synchronizer.GetLocalChanges(localDefinition, cloudDefinition, new InMemoryFileWriter(), null, isRemoteChange: false);

            Assert.Empty(changes);
            Assert.Empty(changeSet.EnvironmentVariableChanges);
        }

        [Fact]
        public void EnvironmentVariable_Create_WhenRemoteChange()
        {
            var schemaName = "cr123";

            var env = new EnvironmentVariableDefinition(
                id: new EnvironmentVariableDefinitionId(Guid.NewGuid()),
                schemaName: new EnvironmentVariableDefinitionSchemaName($"{schemaName}.env1"),
                displayName: "env1",
                defaultValue: "value1",
                valueComponent: null,
                version: 1
            );

            var localDefinition = new BotDefinition.Builder
            {
                Entity = new BotEntity().WithSchemaName(new BotEntitySchemaName(schemaName)),
                EnvironmentVariables = { env }
            }.Build();

            var cloudDefinition = new BotDefinition.Builder
            {
                Entity = new BotEntity().WithSchemaName(new BotEntitySchemaName(schemaName)),
            }.Build();

            var synchronizer = CreateSynchronizer();

            var (changeSet, changes) = synchronizer.GetLocalChanges(localDefinition, cloudDefinition, new InMemoryFileWriter(), null, isRemoteChange: true);

            Assert.Single(changes);
            Assert.Equal(ChangeType.Create, changes[0].ChangeType);
            Assert.Single(changeSet.EnvironmentVariableChanges);
            Assert.IsType<EnvironmentVariableInsert>(changeSet.EnvironmentVariableChanges[0]);
        }

        [Fact]
        public async Task EnvironmentVariable_Update_WhenDifferent()
        {
            var schemaName = "cr123";

            var localEnv = new EnvironmentVariableDefinition(
                 id: new EnvironmentVariableDefinitionId(Guid.NewGuid()),
                 schemaName: new EnvironmentVariableDefinitionSchemaName($"{schemaName}.env1"),
                 displayName: "environment variable 1",
                 defaultValue: "value1",
                 valueComponent: null,
                 version: 1
            );

            var cloudEnv = new EnvironmentVariableDefinition(
                 id: new EnvironmentVariableDefinitionId(Guid.NewGuid()),
                 schemaName: new EnvironmentVariableDefinitionSchemaName($"{schemaName}.env1"),
                 displayName: "environment variable 1 version 2",
                 defaultValue: "cloud",
                 valueComponent: null,
                 version: 1
            );

            var localDefinition = new BotDefinition.Builder
            {
                Entity = new BotEntity().WithSchemaName(new BotEntitySchemaName(schemaName)),
                EnvironmentVariables = { localEnv }
            }.Build();

            var cloudDefinition = new BotDefinition.Builder
            {
                Entity = new BotEntity().WithSchemaName(new BotEntitySchemaName(schemaName)),
                EnvironmentVariables = { cloudEnv }
            }.Build();

            var fileAccessor = new InMemoryFileWriter();
            await fileAccessor.WriteAsync(new AgentFilePath($"environmentvariables/{localEnv.SchemaName.Value}.mcs.yml"), "content", CancellationToken.None);

            var synchronizer = CreateSynchronizer();

            var (changeSet, changes) = synchronizer.GetLocalChanges(localDefinition, cloudDefinition, fileAccessor, null);

            Assert.Single(changes);
            Assert.Equal(ChangeType.Update, changes[0].ChangeType);
            Assert.Single(changeSet.EnvironmentVariableChanges);
            Assert.IsType<EnvironmentVariableUpdate>(changeSet.EnvironmentVariableChanges[0]);
        }

        [Fact]
        public void EnvironmentVariable_Delete_WhenMissingLocally()
        {
            var schemaName = "cr123";
            var defId = new EnvironmentVariableDefinitionId(Guid.NewGuid());
            var valueId = new EnvironmentVariableValueId(Guid.NewGuid());

            var cloudEnv = new EnvironmentVariableDefinition(
                id: defId,
                schemaName: new EnvironmentVariableDefinitionSchemaName($"{schemaName}.env1"),
                displayName: "env1",
                defaultValue: "value",
                valueComponent: new EnvironmentVariableValue(
                    id: valueId,
                    definitionId: defId,
                    value: "value"
                ),
                version: 1
            );

            var localDefinition = new BotDefinition.Builder
            {
                Entity = new BotEntity().WithSchemaName(new BotEntitySchemaName(schemaName)),
            }.Build();

            var cloudDefinition = new BotDefinition.Builder
            {
                Entity = new BotEntity().WithSchemaName(new BotEntitySchemaName(schemaName)),
                EnvironmentVariables = { cloudEnv }
            }.Build();

            var synchronizer = CreateSynchronizer();

            var (changeSet, changes) = synchronizer.GetLocalChanges(localDefinition, cloudDefinition, new InMemoryFileWriter(), null);

            Assert.Single(changes);
            Assert.Equal(ChangeType.Delete, changes[0].ChangeType);
            Assert.Single(changeSet.EnvironmentVariableChanges);
            Assert.IsType<EnvironmentVariableDelete>(changeSet.EnvironmentVariableChanges[0]);
        }

        private WorkspaceSynchronizer CreateSynchronizer()
        {
            return new WorkspaceSynchronizer(
                new SyncMcsFileParser(Microsoft.CopilotStudio.McsCore.LspProjectorService.Instance),
                (Microsoft.CopilotStudio.McsCore.IFileAccessorFactory)new InMemoryFileWriter(),
                Mock.Of<IIslandControlPlaneService>(),
                Mock.Of<ISyncProgress>(),
                new Microsoft.CopilotStudio.McsCore.LspComponentPathResolver());
        }

        private FileAttachmentComponent CreateKnowledgeComponent(string schema = "cr123.knowledge", string name = "File1", string description = "description")
        {
            return new FileAttachmentComponent.Builder
            {
                Id = new BotComponentId(Guid.NewGuid()),
                SchemaName = new FileAttachmentSchemaName(schema),
                DisplayName = name,
                Description = description
            }.Build();
        }

        private BotDefinition CreateDefinition(Guid botId, params BotComponentBase[] components)
        {
            var builder = new BotDefinition.Builder
            {
                Entity = new BotEntity().WithCdsBotId(botId)
            };

            foreach (var c in components)
            {
                builder.Components.Add(c);
            }

            return builder.Build();
        }
    }

    internal sealed class TempDirectory : IDisposable
    {
        public string Path { get; }

        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, true);
            }
            catch
            {
                // ignore errors during cleanup
            }
        }
    }
}
