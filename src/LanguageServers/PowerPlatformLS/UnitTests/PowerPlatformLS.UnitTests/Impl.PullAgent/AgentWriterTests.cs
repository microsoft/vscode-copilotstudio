namespace Microsoft.PowerPlatformLS.UnitTests.Impl.PullAgent
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.Agents.Platform.Content;
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.PowerPlatformLS.Contracts.FileLayout;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Models;
    using Microsoft.PowerPlatformLS.Impl.PullAgent;
    using Microsoft.PowerPlatformLS.Impl.PullAgent.Dataverse;
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

    public class AgentWriterTests
    {
        private static readonly DirectoryPath WorkspaceFolderPath = new DirectoryPath(string.Empty);
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
            var writer = new WorkspaceSynchronizer(new McsFileParser(), filesystem, contentAuthoringService, logger, new LspComponentPathResolver());

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
            var writer = new WorkspaceSynchronizer(new McsFileParser(), filesystem, islandControlPlaneServicMock.Object, lspLoggerMock.Object, new LspComponentPathResolver());

            var ex = await Assert.ThrowsAsync<FileNotFoundException>(() => writer.GetSyncInfoAsync(WorkspaceFolderPath));

            Assert.Contains("conn.json was not found", ex.Message);
        }

        [Fact]
        public async Task GetSyncInfoAsyncThrowsInvalidOperation()
        {
            var filesystem = new InMemoryFileWriter();
            var islandControlPlaneServicMock = new Mock<IIslandControlPlaneService>();
            var lspLoggerMock = new Mock<ILspLogger>();
            var writer = new WorkspaceSynchronizer(new McsFileParser(), filesystem, islandControlPlaneServicMock.Object, lspLoggerMock.Object, new LspComponentPathResolver());

            var accessor = filesystem.Create(WorkspaceFolderPath);
            using (var stream = accessor.OpenWrite(new AgentFilePath(".mcs/conn.json")))
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
            var accessor = filesystem.Create(WorkspaceFolderPath);

            await accessor.WriteAsync(useOldCache ? OldBotCachePath : BotCachePath, content, CancellationToken.None);

            var result = WorkspaceSynchronizer.ReadCloudCacheSnapshot(accessor);

            Assert.NotNull(result);
            Assert.Equal(BotElementKind.BotDefinition, result?.Kind);
            Assert.Single(result?.Flows);
        }

        [Fact]
        public void ReadCloudCacheSnapshotAllowMissing()
        {
            var filesystem = new InMemoryFileWriter();
            var accessor = filesystem.Create(WorkspaceFolderPath);

            var result = WorkspaceSynchronizer.ReadCloudCacheSnapshot(accessor, allowMissing: true);

            Assert.Null(result);
        }

        [Fact]
        public void ReadCloudCacheSnapshotThrowsFileNotFound()
        {
            var filesystem = new InMemoryFileWriter();
            var accessor = filesystem.Create(WorkspaceFolderPath);

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

            var writer = new WorkspaceSynchronizer(new McsFileParser(), filesystem, contentAuthoringService, logger, new LspComponentPathResolver());

            // Test
            var referenceTracker = new ReferenceTracker();

            var dataverseClient = new MockDataverseClient();
            Guid agentId = Guid.NewGuid();
            await writer.CloneChangesAsync(WorkspaceFolderPath, referenceTracker, FakeOperationContext, dataverseClient, agentId, cancel);

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
# Name: Topic2DisplayName 
# This is description line 1.
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

            var writer = new WorkspaceSynchronizer(new McsFileParser(), filesystem, contentAuthoringService, logger, new LspComponentPathResolver());

            // Test
            var referenceTracker = new ReferenceTracker();
            var dataverseClient = new MockDataverseClient();
            Guid agentId = Guid.NewGuid();
            await writer.CloneChangesAsync(WorkspaceFolderPath, referenceTracker, FakeOperationContext, dataverseClient, agentId, cancel);

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
            Assert.Equal("# Name: Display123", currentAgentMcsYml.Trim());
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void KnowledgeFileSyncTest(bool hasKnowledgeFileInLocal)
        {
            var schemaName = new FileAttachmentSchemaName("cr123.knowledge");
            var botId = Guid.NewGuid();

            var fileAttachmentComponentLocal = new FileAttachmentComponent.Builder
            {
                Id = new BotComponentId(Guid.NewGuid()),
                SchemaName = schemaName,
                DisplayName = "File1",
                Description = "description of file 1"
            }.Build();

            var fileAttachmentComponentCloud = new FileAttachmentComponent.Builder
            {
                Id = new BotComponentId(Guid.NewGuid()),
                SchemaName = schemaName,
                DisplayName = "File1",
                Description = "description of file 1"
            }.Build();

            var localDefinitionBuilder = new BotDefinition.Builder
            {
                Entity = new BotEntity().WithCdsBotId(botId)
            };

            if (hasKnowledgeFileInLocal)
            {
                localDefinitionBuilder.Components.Add(fileAttachmentComponentLocal);
            }

            var localDefinition = localDefinitionBuilder.Build();

            var cloudDefinition = new BotDefinition.Builder
            {
                Entity = new BotEntity().WithCdsBotId(botId),
                Components = { fileAttachmentComponentCloud }
            }.Build();

            var synchronizer = new WorkspaceSynchronizer(
                new McsFileParser(),
                new InMemoryFileWriter(),
                Mock.Of<IIslandControlPlaneService>(),
                Mock.Of<ILspLogger>(),
                new LspComponentPathResolver());

            var (changeSet, changes) = synchronizer.GetLocalChanges(localDefinition, cloudDefinition, null);

            // Knowledge file sync change is handled in client and skip in server.
            Assert.Empty(changes);
            Assert.Empty(changeSet.BotComponentChanges);
        }

        [Fact]
        public async Task PullExistingChangesAsync_WithMergeConflict_MergesCorrectly()
        {
            string schemaName = "cr123";
            string topicSchemaName = $"{schemaName}.topic.topicWithMergeConflict";

            var cancel = new CancellationToken();
            var filesystem = new InMemoryFileWriter();
            var islandControlPlaneServiceMock = new Mock<IIslandControlPlaneService>();
            var synchronizer = new WorkspaceSynchronizer(new McsFileParser(), filesystem, islandControlPlaneServiceMock.Object, Mock.Of<ILspLogger>(), new LspComponentPathResolver());

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
            WorkspaceSynchronizer.WriteCloudCache(filesystem.Create(WorkspaceFolderPath), originalDefinition);
            await filesystem.Create(WorkspaceFolderPath).WriteAsync(new AgentFilePath(".mcs/changetoken.txt"), "original_token", cancel);

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
            var mergedDefinition = await synchronizer.PullExistingChangesAsync(WorkspaceFolderPath, FakeOperationContext, previousDefinition, new MockDataverseClient(), Guid.NewGuid(), cancel);

            // Verify the merged definition contains the expected components
            Assert.Equal(
            [
               ".mcs/botdefinition.json",
               ".mcs/changetoken.txt",
               "settings.mcs.yml",
               "topics/topicWithMergeConflict.mcs.yml" // filename should have schema truncated. 
            ], filesystem.Filenames);

            // Verify the topic file contains the merged content with conflict markers
            var pathResolver = new LspComponentPathResolver();
            string actualMergedContent = await filesystem.ReadStringAsync(new AgentFilePath(pathResolver.GetComponentPath(originalComponent, originalDefinition)), cancel);
            string expectedMergedContent = """
# Name: Thank you
# This topic triggers when the user says thank you.
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
            var updatedCache = WorkspaceSynchronizer.ReadCloudCacheSnapshot(filesystem.Create(WorkspaceFolderPath));
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
            var synchronizer = new WorkspaceSynchronizer(new McsFileParser(), filesystem, islandControlPlaneServiceMock.Object, Mock.Of<ILspLogger>(), new LspComponentPathResolver());

            var componentFactory = new TestBotComponentFactory(topicSchemaName);
            var originalComponent = componentFactory.CreateDialogComponent("test dialog component");

            var workflowId = Guid.NewGuid();
            var workflowOriginal = new DataverseClient.WorkflowMetadata
            {
                WorkflowId = workflowId,
                Name = "OriginalWorkflow",
                ClientData = @"{ ""property"": ""original-clientdata"" }"
            };
            var workflowLocal = new DataverseClient.WorkflowMetadata
            {
                WorkflowId = workflowId,
                Name = "LocalWorkflow",
                ClientData = @"{ ""property"": ""local-clientdata"" }"
            };
            var workflowRemote = new DataverseClient.WorkflowMetadata
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

            WorkspaceSynchronizer.WriteCloudCache(filesystem.Create(WorkspaceFolderPath), originalDefinition);
            await filesystem.Create(WorkspaceFolderPath).WriteAsync(new AgentFilePath(".mcs/changetoken.txt"), "original_token", cancel);

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
                Guid.NewGuid(),
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
            var synchronizer = new WorkspaceSynchronizer(new McsFileParser(), filesystem, islandControlPlaneServiceMock.Object, loggerMock, new LspComponentPathResolver());

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
            await filesystem.Create(WorkspaceFolderPath).WriteAsync(new AgentFilePath($"{workflowFolder}/workflow.json"), @"{ ""property"": ""local-clientdata"" }", cancel);
            await filesystem.Create(WorkspaceFolderPath).WriteAsync(new AgentFilePath($"{workflowFolder}/metadata.yml"), $"workflowId: {workflowId}\nname: LocalWorkflow", cancel);

            // Write minimal cloud cache to avoid FileNotFoundException
            var botDefinition = new BotDefinition.Builder
            {
                Entity = new BotEntity().WithSchemaName(new BotEntitySchemaName(schemaName)),
                Components = { },
                Flows = { workflow }
            }.Build();
            WorkspaceSynchronizer.WriteCloudCache(filesystem.Create(WorkspaceFolderPath), botDefinition);

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
                cancel
            );

            var workflowJsonPath = new AgentFilePath($"{workflowFolder}/workflow.json");
            var workflowJsonContent = await filesystem.ReadStringAsync(workflowJsonPath, cancel);
            JsonDocument.Parse(workflowJsonContent);

            islandControlPlaneServiceMock.Verify(s => s.SaveChangesAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<PvaComponentChangeSet>(), cancel), Times.Once);
            Assert.NotNull(savedChangeSet);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task UpsertWorkflowForAgentAsync_InsertOrUpdate(bool isInsert)
        {
            using var tempWorkspace = new TempDirectory();
            var workspacePath = tempWorkspace.Path.Replace("\\", "/");
            var workspaceFolder = new DirectoryPath(workspacePath);
            var islandControlPlaneServiceMock = new Mock<IIslandControlPlaneService>();
            var synchronizer = new WorkspaceSynchronizer(new McsFileParser(), new InMemoryFileWriter(), islandControlPlaneServiceMock.Object, Mock.Of<ILspLogger>(), new LspComponentPathResolver());

            var workflowId = Guid.NewGuid();
            var agentId = Guid.NewGuid();
            var workflowFolder = $"{workspaceFolder}/workflows/{workflowId}";
            Directory.CreateDirectory(workflowFolder.Replace("/", Path.DirectorySeparatorChar.ToString()));

            var workflowJsonPath = $"{workflowFolder}/workflow.json";
            var workflowMetadataPath = $"{workflowFolder}/metadata.yml";

            await File.WriteAllTextAsync(workflowJsonPath.Replace("/", Path.DirectorySeparatorChar.ToString()), @"{ ""property"": ""clientdata"" }");
            await File.WriteAllTextAsync(workflowMetadataPath.Replace("/", Path.DirectorySeparatorChar.ToString()), $"workflowId: {workflowId}\nname: TestWorkflow");

            var mockDataverse = new MockDataverseClient();

            await synchronizer.UpsertWorkflowForAgentAsync(workspaceFolder, mockDataverse, agentId, isInsert, new CancellationToken());

            var workflowCall = mockDataverse.WorkflowCalls.SingleOrDefault();
            Assert.Equal(agentId, workflowCall.AgentId);
            Assert.Equal(workflowId, workflowCall.Metadata.WorkflowId);
            Assert.Equal(isInsert ? "Insert" : "Update", workflowCall.Operation);
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
                new DataverseClient.WorkflowMetadata
                {
                    WorkflowId = workflowId,
                    Name = "TestWorkflow",
                    ClientData = clientData,
                    StateCode = 1
                }
            });

            var filesystem = new InMemoryFileWriter();
            var islandServiceMock = new Mock<IIslandControlPlaneService>();
            var synchronizer = new WorkspaceSynchronizer(new McsFileParser(), filesystem, islandServiceMock.Object, Mock.Of<ILspLogger>(), new LspComponentPathResolver());

            var workflows = await synchronizer.GetWorkflowsAsync(workspaceFolder, mockDataverse, agentId, filesystem, CancellationToken.None);

            Assert.Single(workflows);
            var workflow = workflows[0];

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
        public async Task ProvisionConnectionReferencesAsync_ProvisionsConnections()
        {
            var islandControlPlaneServiceMock = new Mock<IIslandControlPlaneService>();
            var synchronizer = new WorkspaceSynchronizer(new McsFileParser(), new InMemoryFileWriter(), islandControlPlaneServiceMock.Object, Mock.Of<ILspLogger>(), new LspComponentPathResolver());

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
            var synchronizer = new WorkspaceSynchronizer(new McsFileParser(), new InMemoryFileWriter(), islandControlPlaneServiceMock.Object, Mock.Of<ILspLogger>(), new LspComponentPathResolver());

            var definition = new BotComponentCollectionDefinition();
            var mockDataverse = new MockDataverseClientWithConnectionTracking();

            await synchronizer.ProvisionConnectionReferencesAsync(definition, mockDataverse, new CancellationToken());

            Assert.Empty(mockDataverse.ProvisionedConnections);
        }

        [Fact]
        public async Task ProvisionConnectionReferencesAsync_WithNoConnections_DoesNothing()
        {
            var islandControlPlaneServiceMock = new Mock<IIslandControlPlaneService>();
            var synchronizer = new WorkspaceSynchronizer(new McsFileParser(), new InMemoryFileWriter(), islandControlPlaneServiceMock.Object, Mock.Of<ILspLogger>(), new LspComponentPathResolver());

            var botEntity = new BotEntity();
            var definition = new BotDefinition.Builder
            {
                Entity = botEntity
            }.Build();

            var mockDataverse = new MockDataverseClientWithConnectionTracking();

            await synchronizer.ProvisionConnectionReferencesAsync(definition, mockDataverse, new CancellationToken());

            Assert.Empty(mockDataverse.ProvisionedConnections);
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
