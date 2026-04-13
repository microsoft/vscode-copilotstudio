namespace Microsoft.PowerPlatformLS.UnitTests.Impl.PullAgent.Methods
{
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.CopilotStudio.Sync;
    using Microsoft.PowerPlatformLS.Impl.PullAgent;
    using Microsoft.PowerPlatformLS.UnitTests.Impl.Language.CopilotStudio;
    using Microsoft.PowerPlatformLS.UnitTests.TestUtilities;
    using Moq;
    using System;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Xunit;
    using SyncDirectoryPath = Microsoft.CopilotStudio.Sync.DirectoryPath;
    using WorkspaceType = Microsoft.PowerPlatformLS.Impl.PullAgent.WorkspaceType;

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
            synchronizer.Setup(s => s.IsSyncInfoAvailable(It.IsAny<SyncDirectoryPath>())).Returns(false);

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
            synchronizer.Setup(s => s.IsSyncInfoAvailable(It.IsAny<SyncDirectoryPath>())).Returns(true);
            synchronizer.Setup(s => s.GetSyncInfoAsync(It.IsAny<SyncDirectoryPath>())).ReturnsAsync(expectedSyncInfo);

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
    }
}
