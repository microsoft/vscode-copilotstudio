namespace Microsoft.PowerPlatformLS.UnitTests.Impl.PullAgent.Methods
{
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.Agents.ObjectModel;
    using Microsoft.CopilotStudio.Sync;
    using Microsoft.CopilotStudio.Sync.Dataverse;
    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Impl.PullAgent;
    using Microsoft.PowerPlatformLS.UnitTests.Impl.Language.CopilotStudio;
    using Microsoft.PowerPlatformLS.UnitTests.TestUtilities;
    using Moq;
    using System;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Xunit;
    using Microsoft.CopilotStudio.McsCore;
    using WorkspaceType = Microsoft.PowerPlatformLS.Impl.PullAgent.WorkspaceType;
    using InMemoryFileAccessorFactory = Microsoft.PowerPlatformLS.UnitTests.TestUtilities.InMemoryFileAccessorFactory;

    public class GetWorkspaceDetailsHandlerTests
    {
        private const string TestDataPath = "TestData";
        private const string WorkspacePath = "Workspace/LocalWorkspace";
        private const string TopicsPath = "topics/Goodbye.mcs.yml";

        [Fact]
        public async Task ReturnsAgentWorkspaceDetails_NoSyncInfo()
        {
            // Arrange — use existing test workspace that has an agent definition
            var workspacePath = Path.GetFullPath(Path.Combine(TestDataPath, WorkspacePath));
            var world = new World(workspacePath);
            var doc = world.GetDocument(Path.Combine(workspacePath, TopicsPath));
            var requestContext = world.GetRequestContext(doc, 0);

            // Mock synchronizer: no sync info available (no conn.json)
            var synchronizer = new Mock<Microsoft.CopilotStudio.Sync.IWorkspaceSynchronizer>();
            synchronizer.Setup(s => s.IsSyncInfoAvailable(It.IsAny<DirectoryPath>())).Returns(false);

            // Mock file accessor: no icon
            var fileAccessor = new InMemoryFileWriter();
            var fileFactory = new InMemoryFileAccessorFactory();

            var logger = new Mock<ILspLogger>();

            var handler = new GetWorkspaceDetailsHandler(fileFactory, synchronizer.Object, logger.Object);

            // Act
            var request = new GetWorkspaceDetailsParams { WorkspaceUri = new Uri(workspacePath) };
            var result = await handler.HandleRequestAsync(request, requestContext, CancellationToken.None);

            // Assert
            Assert.NotNull(result);
            Assert.Equal(WorkspaceType.Agent, result.Type);
            Assert.NotNull(result.DisplayName);
            Assert.Equal("cree9_agent", result.SchemaName);
            Assert.Null(result.SyncInfo);
        }

        [Fact]
        public async Task ReturnsAgentWorkspaceDetails_WithSyncInfo()
        {
            // Arrange — use existing test workspace
            var workspacePath = Path.GetFullPath(Path.Combine(TestDataPath, WorkspacePath));
            var world = new World(workspacePath);
            var doc = world.GetDocument(Path.Combine(workspacePath, TopicsPath));
            var requestContext = world.GetRequestContext(doc, 0);

            var expectedSyncInfo = new AgentSyncInfo
            {
                AgentId = Guid.NewGuid(),
                DataverseEndpoint = new Uri("https://org.crm.dynamics.com"),
                EnvironmentId = "env-01",
            };

            // Mock synchronizer: sync info IS available
            var synchronizer = new Mock<Microsoft.CopilotStudio.Sync.IWorkspaceSynchronizer>();
            synchronizer.Setup(s => s.IsSyncInfoAvailable(It.IsAny<DirectoryPath>())).Returns(true);
            synchronizer.Setup(s => s.GetSyncInfoAsync(It.IsAny<DirectoryPath>())).ReturnsAsync(expectedSyncInfo);

            var fileFactory = new InMemoryFileAccessorFactory();
            var logger = new Mock<ILspLogger>();

            var handler = new GetWorkspaceDetailsHandler(fileFactory, synchronizer.Object, logger.Object);

            // Act
            var request = new GetWorkspaceDetailsParams { WorkspaceUri = new Uri(workspacePath) };
            var result = await handler.HandleRequestAsync(request, requestContext, CancellationToken.None);

            // Assert
            Assert.NotNull(result);
            Assert.Equal(WorkspaceType.Agent, result.Type);
            Assert.NotNull(result.SyncInfo);
            Assert.Equal(expectedSyncInfo.AgentId, result.SyncInfo!.AgentId);
            Assert.Equal(expectedSyncInfo.EnvironmentId, result.SyncInfo.EnvironmentId);
        }

        [Theory]
        [InlineData("c:/code/3wf/", "3wf")]
        [InlineData("c:/code/3wf", "3wf")]
        [InlineData("c:/code/My Flows/", "My Flows")]
        public void AWorkspaceWithoutADefinitionNameFallsBackToItsFolderName(string folderPath, string expected) =>
            Assert.Equal(expected, GetWorkspaceDetailsHandler.FolderNameOf(folderPath));

        [Fact]
        public async Task AStandaloneWorkflowWorkspaceIsReportedAsAWorkflowEvenThoughItCompilesToABotDefinition()
        {
            var parent = NewStandaloneWorkspaceParent();

            try
            {
                var root = Path.Combine(parent, "S5D1WF1");
                var world = new World(root);
                var workspace = world.GetWorkspace();
                workspace.BuildCompilationModel();

                var synchronizer = new Mock<Microsoft.CopilotStudio.Sync.IWorkspaceSynchronizer>();
                synchronizer.Setup(s => s.IsSyncInfoAvailable(It.IsAny<DirectoryPath>())).Returns(false);

                var handler = new GetWorkspaceDetailsHandler(new FileAccessorFactory(), synchronizer.Object, Mock.Of<ILspLogger>());
                var context = new RequestContext(world.GetRequiredService<ILanguageAbstraction>(), workspace, null, 0);
                var request = new GetWorkspaceDetailsParams { WorkspaceUri = new Uri(root.Replace('\\', '/')) };

                var result = await handler.HandleRequestAsync(request, context, CancellationToken.None);

                Assert.IsAssignableFrom<BotDefinition>(workspace.Definition);
                Assert.Equal(WorkspaceType.StandaloneWorkflow, result.Type);
                Assert.Null(result.SchemaName);
                Assert.Equal("S5D1WF1", result.DisplayName);
            }
            finally
            {
                Directory.Delete(parent, recursive: true);
            }
        }

        private static string NewStandaloneWorkspaceParent()
        {
            var parent = Path.Combine(Path.GetTempPath(), "mcs-wf-" + Guid.NewGuid().ToString("N"));
            var root = Path.Combine(parent, "S5D1WF1");
            var workflowId = Guid.NewGuid();
            var workflowFolder = Path.Combine(root, WorkflowWorkspace.WorkflowsFolderName, WorkflowWorkspace.FolderNameFor("S5D1WF1", workflowId));

            Directory.CreateDirectory(workflowFolder);

            var accessor = WorkflowWorkspace.OpenWorkspace(root);
            WorkflowWorkspace.EnsureLayoutMarker(accessor);
            WorkflowWorkspace.EnsureProjectFile(accessor);

            File.WriteAllText(Path.Combine(workflowFolder, WorkflowWorkspace.DefinitionFileName), "{}");
            File.WriteAllText(Path.Combine(workflowFolder, WorkflowWorkspace.MetadataFileName), WorkflowMetadataFile.Write(new SyncDataverseClient.WorkflowMetadata
            {
                WorkflowId = workflowId,
                Name = "S5D1WF1",
                ClientData = "{}",
            }));

            return parent;
        }
    }
}
