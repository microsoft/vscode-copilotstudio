namespace Microsoft.PowerPlatformLS.UnitTests.Impl.PullAgent.Methods
{
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.CopilotStudio.Sync;
    using Microsoft.PowerPlatformLS.Impl.PullAgent;
    using Microsoft.PowerPlatformLS.UnitTests.Impl.Language.CopilotStudio;
    using Moq;
    using System;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Xunit;
    using IMcsWorkspace = Microsoft.PowerPlatformLS.Contracts.FileLayout.IMcsWorkspace;

    public class CreateConnectionReferenceHandlerTests
    {
        [Fact]
        public async Task CreateConnectionReference_DeclaresLocallyWithoutReadingRemoteViewsOrWritingTheirCache()
        {
            var workspacePath = Path.GetFullPath(Path.Combine("TestData", "Workspace", "LocalWorkspace"));
            var world = new World(workspacePath);
            var document = world.GetDocument(Path.Combine(workspacePath, "topics", "Goodbye.mcs.yml"));
            Assert.NotNull(document);
            var context = world.GetRequestContext(document!, 0);
            var workspace = Assert.IsAssignableFrom<IMcsWorkspace>(context.Workspace);
            var connections = new Mock<IConnectionManagementService>(MockBehavior.Strict);
            connections.Setup(service => service.CreateConnectionReferenceForConnectorAsync(
                    workspace.FolderPath, workspace.Definition, "shared_office365", CancellationToken.None))
                .ReturnsAsync("agent.shared_office365.new");
            var synchronizer = new Mock<IWorkspaceSynchronizer>(MockBehavior.Strict);
            synchronizer.Setup(service => service.IsSyncInfoAvailable(workspace.FolderPath)).Returns(false);
            var handler = new CreateConnectionReferenceHandler(
                connections.Object, new Mock<ILspLogger>().Object, synchronizer.Object);
            var request = new CreateConnectionReferenceRequest
            {
                WorkspaceUri = new Uri(workspacePath),
                ConnectorInternalId = "shared_office365",
                AccountInfo = new AccountInfo(),
                EnvironmentInfo = new EnvironmentInfo
                {
                    EnvironmentId = "workspace-environment",
                    DisplayName = "Workspace environment",
                    DataverseUrl = "https://workspace.example.com",
                    AgentManagementUrl = "https://workspace-management.example.com",
                },
                SolutionVersions = new SolutionInfo(),
                CopilotStudioAccessToken = string.Empty,
                DataverseAccessToken = string.Empty,
            };

            var response = await handler.HandleRequestAsync(request, context, CancellationToken.None);

            Assert.Equal(200, response.Code);
            Assert.Equal("agent.shared_office365.new", response.LogicalName);
            connections.Verify(service => service.CreateConnectionReferenceForConnectorAsync(
                workspace.FolderPath, workspace.Definition, "shared_office365", CancellationToken.None), Times.Once);
            connections.VerifyNoOtherCalls();
        }
    }
}
