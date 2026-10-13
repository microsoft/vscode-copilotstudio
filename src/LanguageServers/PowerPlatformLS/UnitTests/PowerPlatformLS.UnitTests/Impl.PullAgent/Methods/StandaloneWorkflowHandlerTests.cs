namespace Microsoft.PowerPlatformLS.UnitTests.Impl.PullAgent.Methods
{
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.CopilotStudio.Sync;
    using Microsoft.CopilotStudio.Sync.Dataverse;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Impl.PullAgent;
    using Microsoft.PowerPlatformLS.Impl.PullAgent.Auth;
    using Moq;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Xunit;
    using TestFileAccessorFactory = Microsoft.PowerPlatformLS.UnitTests.TestUtilities.InMemoryFileAccessorFactory;

    public class StandaloneWorkflowHandlerTests
    {
        private const string EnvironmentId = "TestEnvironment";

        private static readonly Guid WorkflowId = Guid.Parse("11111111-2222-3333-4444-555555555555");

        private static readonly AccountInfo Account = new AccountInfo { AccountId = "account", TenantId = Guid.NewGuid(), AccountEmail = "email" };

        private static readonly EnvironmentInfo Environment = new EnvironmentInfo
        {
            DataverseUrl = "https://test.crm.dynamics.com",
            AgentManagementUrl = "https://test.agentmanagement.com",
            EnvironmentId = EnvironmentId,
            DisplayName = "Test Environment",
        };

        private static readonly SolutionInfo Solutions = new SolutionInfo { CopilotStudioSolutionVersion = new Version(1, 0, 0, 0) };

        private static ListStandaloneWorkflowsRequest ListRequest(string? nameFilter = null, int? maximumCount = null) => new ListStandaloneWorkflowsRequest
        {
            AccountInfo = Account,
            EnvironmentInfo = Environment,
            SolutionVersions = Solutions,
            DataverseAccessToken = "dv-token",
            CopilotStudioAccessToken = "cs-token",
            NameFilter = nameFilter,
            MaximumCount = maximumCount,
        };

        private static CloneStandaloneWorkflowRequest CloneRequest() => new CloneStandaloneWorkflowRequest
        {
            AccountInfo = Account,
            EnvironmentInfo = Environment,
            SolutionVersions = Solutions,
            DataverseAccessToken = "dv-token",
            CopilotStudioAccessToken = "cs-token",
            WorkflowId = WorkflowId,
            RootFolder = new Uri(Path.Combine(Path.GetTempPath(), "mcs-standalone-" + Guid.NewGuid().ToString("N"))),
        };

        private static StandaloneWorkflowDocument Document() => new StandaloneWorkflowDocument
        {
            WorkflowId = WorkflowId,
            Name = "My Flow",
            Description = "a flow",
            StateCode = 1,
            ConnectionReferences = new[] { "new_weather" },
        };

        private static ListStandaloneWorkflowsHandler ListHandler(Mock<IStandaloneWorkflowSynchronizer> synchronizer) => new ListStandaloneWorkflowsHandler(
            new Mock<IIslandControlPlaneService>().Object,
            new TestTokenManager(),
            new Mock<ISyncDataverseClient>().Object,
            new LspDataverseHttpClientAccessor(new Mock<ISyncAuthProvider>().Object),
            synchronizer.Object,
            new Mock<IWorkspaceSynchronizer>().Object,
            new TestFileAccessorFactory(),
            new Mock<ILspLogger>().Object);

        private static CloneStandaloneWorkflowHandler CloneHandler(Mock<IStandaloneWorkflowSynchronizer> synchronizer) =>
            CloneHandler(synchronizer, new Mock<IWorkspaceSynchronizer>());

        private static CloneStandaloneWorkflowHandler CloneHandler(Mock<IStandaloneWorkflowSynchronizer> synchronizer, Mock<IWorkspaceSynchronizer> workspaceSynchronizer) => new CloneStandaloneWorkflowHandler(
            new Mock<IIslandControlPlaneService>().Object,
            new TestTokenManager(),
            new Mock<ISyncDataverseClient>().Object,
            new LspDataverseHttpClientAccessor(new Mock<ISyncAuthProvider>().Object),
            synchronizer.Object,
            workspaceSynchronizer.Object,
            new TestFileAccessorFactory(),
            new Mock<ILspLogger>().Object);

        [Fact]
        public async Task List_ProjectsEveryWorkflowTheEnvironmentHolds()
        {
            var synchronizer = new Mock<IStandaloneWorkflowSynchronizer>();
            synchronizer
                .Setup(s => s.ListAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<StandaloneWorkflowDocument> { Document() });

            var response = await ListHandler(synchronizer).HandleRequestAsync(ListRequest(), default, CancellationToken.None);

            Assert.Equal(200, response.Code);
            var workflow = Assert.Single(response.Workflows);
            Assert.Equal(WorkflowId, workflow.WorkflowId);
            Assert.Equal("My Flow", workflow.Name);
            Assert.True(workflow.IsActivated);
            Assert.Equal("new_weather", Assert.Single(workflow.ConnectionReferences));
        }

        [Fact]
        public async Task List_PassesTheFilterAndLimitThrough()
        {
            var synchronizer = new Mock<IStandaloneWorkflowSynchronizer>();
            synchronizer
                .Setup(s => s.ListAsync("weather", 5, It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<StandaloneWorkflowDocument>());

            var response = await ListHandler(synchronizer).HandleRequestAsync(ListRequest("weather", 5), default, CancellationToken.None);

            Assert.Equal(200, response.Code);
            synchronizer.Verify(s => s.ListAsync("weather", 5, It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task List_WhenTheServiceFails_ReportsTheFailureInsteadOfThrowing()
        {
            var synchronizer = new Mock<IStandaloneWorkflowSynchronizer>();
            synchronizer
                .Setup(s => s.ListAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("service refused"));

            var response = await ListHandler(synchronizer).HandleRequestAsync(ListRequest(), default, CancellationToken.None);

            Assert.NotEqual(200, response.Code);
            Assert.Empty(response.Workflows);
        }

        [Fact]
        public void List_ReportsItsImmutabilityThroughTheHandlerContract()
        {
            IMethodHandler handler = ListHandler(new Mock<IStandaloneWorkflowSynchronizer>());

            Assert.False(handler.MutatesSolutionState);
        }

        [Fact]
        public async Task Clone_DownloadsIntoAProjectFolderBeneathTheRequestedRoot()
        {
            var synchronizer = new Mock<IStandaloneWorkflowSynchronizer>();
            synchronizer
                .Setup(s => s.DownloadProjectAsync(WorkflowId, It.IsAny<string>(), EnvironmentId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(Project("c:/ws/MyFlow"));

            var response = await CloneHandler(synchronizer).HandleRequestAsync(CloneRequest(), default, CancellationToken.None);

            Assert.Equal(200, response.Code);
            Assert.True(response.Changed);
            Assert.Equal("MyFlow", response.ProjectFolder);
            Assert.Equal("MyFlow/workflows/MyFlow-" + WorkflowId, response.FolderName);
            Assert.Equal(WorkflowId, response.Workflow!.WorkflowId);
        }

        private static StandaloneWorkflowProjectDownload Project(string projectRoot) => new StandaloneWorkflowProjectDownload
        {
            Download = new StandaloneWorkflowDownload { Workflow = Document(), Folder = "workflows/MyFlow-" + WorkflowId, Changed = true },
            ProjectFolder = "MyFlow",
            ProjectRoot = projectRoot,
        };

        [Fact]
        public async Task Clone_WhenTheWorkflowIsAbsent_ReportsTheFailure()
        {
            var synchronizer = new Mock<IStandaloneWorkflowSynchronizer>();
            synchronizer
                .Setup(s => s.DownloadProjectAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("no such workflow"));

            var response = await CloneHandler(synchronizer).HandleRequestAsync(CloneRequest(), default, CancellationToken.None);

            Assert.NotEqual(200, response.Code);
            Assert.Null(response.Workflow);
        }

        [Fact]
        public void Clone_MutatesTheSolutionState()
        {
            IMethodHandler handler = CloneHandler(new Mock<IStandaloneWorkflowSynchronizer>());

            Assert.True(handler.MutatesSolutionState);
        }

        private static async Task<AgentSyncInfo> CloneAndCaptureSyncInfoAsync()
        {
            var syncInfo = await CloneAndCaptureSaveAsync();

            return syncInfo.SyncInfo;
        }

        private static async Task<(DirectoryPath Folder, AgentSyncInfo SyncInfo)> CloneAndCaptureSaveAsync()
        {
            var synchronizer = new Mock<IStandaloneWorkflowSynchronizer>();
            synchronizer
                .Setup(s => s.DownloadProjectAsync(WorkflowId, It.IsAny<string>(), EnvironmentId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(Project("c:/ws/MyFlow"));

            var workspaceSynchronizer = new Mock<IWorkspaceSynchronizer>();
            AgentSyncInfo? captured = null;
            DirectoryPath capturedFolder = default;
            workspaceSynchronizer
                .Setup(s => s.SaveSyncInfoAsync(It.IsAny<DirectoryPath>(), It.IsAny<AgentSyncInfo>()))
                .Callback<DirectoryPath, AgentSyncInfo>((folder, syncInfo) =>
                {
                    capturedFolder = folder;
                    captured = syncInfo;
                })
                .Returns(Task.CompletedTask);

            var response = await CloneHandler(synchronizer, workspaceSynchronizer).HandleRequestAsync(CloneRequest(), default, CancellationToken.None);

            Assert.Equal(200, response.Code);

            return (capturedFolder, captured!);
        }

        [Fact]
        public async Task Clone_BindsTheProjectFolderRatherThanTheRoot()
        {
            var (folder, _) = await CloneAndCaptureSaveAsync();

            Assert.Equal("c:/ws/MyFlow", folder.ToString().TrimEnd('/'));
        }

        [Fact]
        public async Task Clone_BindsTheWorkspaceToTheEnvironmentItCameFrom()
        {
            var syncInfo = await CloneAndCaptureSyncInfoAsync();

            Assert.NotNull(syncInfo);
            Assert.Equal(EnvironmentId, syncInfo.EnvironmentId);
            Assert.Equal(Environment.DisplayName, syncInfo.EnvironmentDisplayName);
            Assert.Equal(new Uri(Environment.DataverseUrl), syncInfo.DataverseEndpoint);
            Assert.Equal(Account.AccountId, syncInfo.AccountInfo!.AccountId);
        }

        [Fact]
        public async Task Clone_BindsAWorkspaceThatHoldsNoAgent()
        {
            var syncInfo = await CloneAndCaptureSyncInfoAsync();

            Assert.Null(syncInfo.AgentId);
            Assert.Null(syncInfo.ComponentCollectionId);
            Assert.Throws<InvalidOperationException>(() => syncInfo.BotReference);
        }

        [Fact]
        public async Task Clone_PopulatesTheAgentManagementEndpointChangeTrackingRequires()
        {
            var syncInfo = await CloneAndCaptureSyncInfoAsync();

            Assert.Equal(new Uri(Environment.AgentManagementUrl), syncInfo.AgentManagementEndpoint);
        }

        private static DeleteStandaloneWorkflowRequest DeleteRequest() => new DeleteStandaloneWorkflowRequest
        {
            AccountInfo = Account,
            EnvironmentInfo = Environment,
            SolutionVersions = Solutions,
            DataverseAccessToken = "dv-token",
            CopilotStudioAccessToken = "cs-token",
            WorkflowId = WorkflowId,
        };

        private static DeleteStandaloneWorkflowHandler DeleteHandler(Mock<IStandaloneWorkflowSynchronizer> synchronizer) => new DeleteStandaloneWorkflowHandler(
            new Mock<IIslandControlPlaneService>().Object,
            new TestTokenManager(),
            new Mock<ISyncDataverseClient>().Object,
            new LspDataverseHttpClientAccessor(new Mock<ISyncAuthProvider>().Object),
            synchronizer.Object,
            new Mock<IWorkspaceSynchronizer>().Object,
            new TestFileAccessorFactory(),
            new Mock<ILspLogger>().Object);

        [Fact]
        public async Task Delete_RemovesTheCloudRowAndLeavesTheWorkspaceAlone()
        {
            var synchronizer = new Mock<IStandaloneWorkflowSynchronizer>();
            synchronizer
                .Setup(s => s.DeleteAsync(WorkflowId, null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            var response = await DeleteHandler(synchronizer).HandleRequestAsync(DeleteRequest(), default, CancellationToken.None);

            Assert.Equal(200, response.Code);
            Assert.True(response.Deleted);
            synchronizer.Verify(s => s.DeleteAsync(WorkflowId, null, It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task Delete_OfAnAlreadyAbsentFlow_Succeeds()
        {
            var synchronizer = new Mock<IStandaloneWorkflowSynchronizer>();
            synchronizer
                .Setup(s => s.DeleteAsync(It.IsAny<Guid>(), It.IsAny<IFileAccessor?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);

            var response = await DeleteHandler(synchronizer).HandleRequestAsync(DeleteRequest(), default, CancellationToken.None);

            Assert.Equal(200, response.Code);
            Assert.False(response.Deleted);
        }

        [Fact]
        public void EnvironmentFirstRequests_DoNotNeedAWorkspaceContext()
        {
            Assert.IsAssignableFrom<IDefaultContextRequest>(ListRequest());
            Assert.IsAssignableFrom<IDefaultContextRequest>(CloneRequest());
            Assert.IsAssignableFrom<IDefaultContextRequest>(DeleteRequest());
            Assert.IsAssignableFrom<IDefaultContextRequest>(new CreateStandaloneWorkflowRequest { Name = "My Flow", RootFolder = new Uri("file:///c:/ws") });
        }

        [Fact]
        public async Task Create_ScaffoldsAWorkflowWorkspaceThatDeclaresItsKind()
        {
            var root = Path.Combine(Path.GetTempPath(), "mcs-create-" + Guid.NewGuid().ToString("N"));

            try
            {
                var request = new CreateStandaloneWorkflowRequest { Name = "My Flow", RootFolder = new Uri(root) };
                var response = await new CreateStandaloneWorkflowHandler(new Mock<ILspLogger>().Object).HandleRequestAsync(request, default, CancellationToken.None);
                var projectRoot = Path.Combine(root, "MyFlow");

                Assert.Equal(200, response.Code);
                Assert.NotEqual(Guid.Empty, response.WorkflowId);
                Assert.Equal("MyFlow", response.ProjectFolder);
                Assert.True(File.Exists(Path.Combine(projectRoot, WorkflowWorkspace.ProjectFileName)));
                Assert.Equal(WorkspaceKind.StandaloneWorkflow, WorkspaceClassifier.DetectKind(projectRoot));
                Assert.Equal(WorkspaceKind.StandaloneWorkflow, WorkspaceClassifier.DetectKind(Path.Combine(root, response.ProjectFolder!)));
                Assert.Contains($"MyFlow/{WorkflowWorkspace.ProjectFileName}", response.WrittenFiles);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public async Task Create_PlacesTheWorkflowWhereTheResponseReportsIt()
        {
            var root = Path.Combine(Path.GetTempPath(), "mcs-create-" + Guid.NewGuid().ToString("N"));

            try
            {
                var request = new CreateStandaloneWorkflowRequest { Name = "My Flow", RootFolder = new Uri(root) };
                var response = await new CreateStandaloneWorkflowHandler(new Mock<ILspLogger>().Object).HandleRequestAsync(request, default, CancellationToken.None);

                Assert.Equal($"MyFlow/workflows/MyFlow-{response.WorkflowId}", response.FolderName);
                Assert.True(File.Exists(Path.Combine(root, Path.Combine(response.FolderName!.Split('/')), WorkflowWorkspace.DefinitionFileName)));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public async Task Create_TwiceIntoOneRoot_GivesEachWorkflowItsOwnPushableProject()
        {
            var root = Path.Combine(Path.GetTempPath(), "mcs-create-" + Guid.NewGuid().ToString("N"));

            try
            {
                var handler = new CreateStandaloneWorkflowHandler(new Mock<ILspLogger>().Object);
                var first = await handler.HandleRequestAsync(new CreateStandaloneWorkflowRequest { Name = "My Flow", RootFolder = new Uri(root) }, default, CancellationToken.None);
                var second = await handler.HandleRequestAsync(new CreateStandaloneWorkflowRequest { Name = "My Flow", RootFolder = new Uri(root) }, default, CancellationToken.None);

                Assert.Equal(200, first.Code);
                Assert.Equal(200, second.Code);
                Assert.Equal("MyFlow", first.FolderName!.Split('/')[0]);
                Assert.Equal($"MyFlow-{second.WorkflowId}", second.FolderName!.Split('/')[0]);
                Assert.Single(WorkflowWorkspace.FindAll(Path.Combine(root, "MyFlow")));
                Assert.Single(WorkflowWorkspace.FindAll(Path.Combine(root, $"MyFlow-{second.WorkflowId}")));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public async Task Create_BesideAnAgentProjectOfTheSameName_ClonesIntoItsOwnProject()
        {
            var root = Path.Combine(Path.GetTempPath(), "mcs-create-" + Guid.NewGuid().ToString("N"));

            try
            {
                Directory.CreateDirectory(Path.Combine(root, "MyFlow"));
                File.WriteAllText(Path.Combine(root, "MyFlow", "settings.mcs.yml"), "displayName: Agent");

                var request = new CreateStandaloneWorkflowRequest { Name = "My Flow", RootFolder = new Uri(root) };
                var response = await new CreateStandaloneWorkflowHandler(new Mock<ILspLogger>().Object).HandleRequestAsync(request, default, CancellationToken.None);

                Assert.Equal(200, response.Code);
                Assert.Equal($"MyFlow-{response.WorkflowId}", response.FolderName!.Split('/')[0]);
                Assert.Equal(WorkspaceKind.Agent, WorkspaceClassifier.DetectKind(Path.Combine(root, "MyFlow")));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public async Task Create_LeavesUnrelatedContentAlone()
        {
            var root = Path.Combine(Path.GetTempPath(), "mcs-create-" + Guid.NewGuid().ToString("N"));

            try
            {
                Directory.CreateDirectory(Path.Combine(root, "MyFlow"));
                File.WriteAllText(Path.Combine(root, "MyFlow", "notes.txt"), "mine");

                var request = new CreateStandaloneWorkflowRequest { Name = "My Flow", RootFolder = new Uri(root) };
                var response = await new CreateStandaloneWorkflowHandler(new Mock<ILspLogger>().Object).HandleRequestAsync(request, default, CancellationToken.None);

                Assert.Equal(200, response.Code);
                Assert.Equal($"MyFlow-{response.WorkflowId}", response.FolderName!.Split('/')[0]);
                Assert.Equal("mine", File.ReadAllText(Path.Combine(root, "MyFlow", "notes.txt")));
                Assert.False(File.Exists(Path.Combine(root, "MyFlow", WorkflowWorkspace.ProjectFileName)));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public async Task Create_DoesNotBindTheWorkspaceToAnyEnvironment()
        {
            var root = Path.Combine(Path.GetTempPath(), "mcs-create-" + Guid.NewGuid().ToString("N"));

            try
            {
                var request = new CreateStandaloneWorkflowRequest { Name = "My Flow", RootFolder = new Uri(root) };

                await new CreateStandaloneWorkflowHandler(new Mock<ILspLogger>().Object).HandleRequestAsync(request, default, CancellationToken.None);

                Assert.False(File.Exists(Path.Combine(root, "MyFlow", ".mcs", "conn.json")));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
