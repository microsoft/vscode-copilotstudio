namespace Microsoft.PowerPlatformLS.UnitTests.Impl.PullAgent.Methods
{
    using Microsoft.CopilotStudio.Sync;
    using Microsoft.CopilotStudio.Sync.Dataverse;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Impl.PullAgent;
    using Microsoft.PowerPlatformLS.Impl.PullAgent.Auth;
    using Microsoft.PowerPlatformLS.UnitTests.Impl.Language.CopilotStudio;
    using Moq;
    using System;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Xunit;

    public class SyncPullStandaloneGuardTests
    {
        private const string TestDataPath = "TestData";
        private const string EnvironmentId = "TestEnvironment";
        private const string TopicsPath = "topics/Goodbye.mcs.yml";

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task SyncPull_StandaloneWorkflowWorkspace_PropagatesTheOverwriteDecision(bool overwriteLocalChanges)
        {
            var (requestContext, request) = CreateSetup();
            request.OverwriteLocalChanges = overwriteLocalChanges;
            StandaloneWorkflowDownloadOptions? captured = null;
            var standalone = StandaloneSynchronizer(options => captured = options);

            var handler = CreateHandler(standalone.Object, requestContext);

            var response = await handler.HandleRequestAsync(request, requestContext, CancellationToken.None);

            Assert.Equal(200, response.Code);
            Assert.Equal(overwriteLocalChanges, captured!.OverwriteLocalChanges);
        }

        [Fact]
        public async Task SyncPull_StandaloneWorkflowWorkspace_DefaultsToKeepingLocalChanges()
        {
            var (requestContext, request) = CreateSetup();
            StandaloneWorkflowDownloadOptions? captured = null;
            var standalone = StandaloneSynchronizer(options => captured = options);

            await CreateHandler(standalone.Object, requestContext).HandleRequestAsync(request, requestContext, CancellationToken.None);

            Assert.False(captured!.OverwriteLocalChanges);
        }

        [Fact]
        public async Task SyncPull_WhenTheWorkspaceHoldsUnpushedChanges_IsRefusedWithAConflictTheClientCanRecognize()
        {
            var (requestContext, request) = CreateSetup();
            var standalone = new Mock<IStandaloneWorkflowSynchronizer>();

            standalone
                .Setup(service => service.ReadLocalAsync(It.IsAny<Microsoft.CopilotStudio.McsCore.IFileAccessor>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new StandaloneWorkflowDocument { WorkflowId = Guid.NewGuid(), Name = "My Flow" });

            standalone
                .Setup(service => service.PullAsync(It.IsAny<Guid>(), It.IsAny<Microsoft.CopilotStudio.McsCore.IFileAccessor>(), It.IsAny<CancellationToken>(), It.IsAny<StandaloneWorkflowDownloadOptions?>()))
                .ThrowsAsync(new StandaloneWorkflowException(StandaloneWorkflowFailure.WorkspaceHoldsUnpushedChanges, "'My Flow-1' holds changes the environment has not seen."));

            var response = await CreateHandler(standalone.Object, requestContext).HandleRequestAsync(request, requestContext, CancellationToken.None);

            Assert.Equal(409, response.Code);
            Assert.Contains("My Flow-1", response.Message, StringComparison.Ordinal);
        }

        private static Mock<IStandaloneWorkflowSynchronizer> StandaloneSynchronizer(Action<StandaloneWorkflowDownloadOptions?> capture)
        {
            var standalone = new Mock<IStandaloneWorkflowSynchronizer>();

            standalone
                .Setup(service => service.ReadLocalAsync(It.IsAny<Microsoft.CopilotStudio.McsCore.IFileAccessor>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new StandaloneWorkflowDocument { WorkflowId = Guid.NewGuid(), Name = "My Flow" });

            standalone
                .Setup(service => service.PullAsync(It.IsAny<Guid>(), It.IsAny<Microsoft.CopilotStudio.McsCore.IFileAccessor>(), It.IsAny<CancellationToken>(), It.IsAny<StandaloneWorkflowDownloadOptions?>()))
                .Callback((Guid _, Microsoft.CopilotStudio.McsCore.IFileAccessor _, CancellationToken _, StandaloneWorkflowDownloadOptions? options) => capture(options))
                .ReturnsAsync(new StandaloneWorkflowPull());

            return standalone;
        }

        private static (RequestContext, SyncAgentRequest) CreateSetup()
        {
            var workspacePath = Path.GetFullPath(Path.Combine(TestDataPath, "Workspace/UnrecognizedTemplateWorkspace"));
            var world = new World(workspacePath);
            var doc = world.GetDocument(Path.Combine(workspacePath, TopicsPath));
            var requestContext = world.GetRequestContext(doc!, 0);

            var request = new SyncAgentRequest
            {
                WorkspaceUri = new Uri(workspacePath),
                AccountInfo = new AccountInfo
                {
                    AccountId = "testAccount",
                    TenantId = Guid.NewGuid(),
                    AccountEmail = "testEmail",
                },
                EnvironmentInfo = new EnvironmentInfo
                {
                    DataverseUrl = "https://test.crm.dynamics.com",
                    AgentManagementUrl = "https://test.agentmanagement.com",
                    EnvironmentId = EnvironmentId,
                    DisplayName = "Test Environment",
                },
                SolutionVersions = new SolutionInfo { CopilotStudioSolutionVersion = new Version(1, 0, 0, 0) },
                CopilotStudioAccessToken = "CopilotStudioToken",
                DataverseAccessToken = "DataverseToken",
            };

            return (requestContext, request);
        }

        private static SyncPullHandler CreateHandler(IStandaloneWorkflowSynchronizer standaloneSynchronizer, RequestContext requestContext)
        {
            var mockAuthProvider = new Mock<ISyncAuthProvider>();
            mockAuthProvider.Setup(provider => provider.AcquireTokenAsync(It.IsAny<Uri>(), It.IsAny<CancellationToken>())).ReturnsAsync("mock-token");

            return new SyncPullHandler(
                new Mock<IIslandControlPlaneService>().Object,
                new PushTrackingSynchronizer(),
                new TestTokenManager(),
                new MockDataverseClient(),
                new LspDataverseHttpClientAccessor(mockAuthProvider.Object),
                Mock.Of<IOperationContextProvider>(),
                standaloneSynchronizer,
                StandaloneWorkflowFactory(((global::Microsoft.PowerPlatformLS.Contracts.FileLayout.IMcsWorkspace)requestContext.Workspace).FolderPath),
                Mock.Of<global::Microsoft.PowerPlatformLS.Contracts.Internal.Common.IClientWorkspaceFileProvider>(),
                new Mock<Microsoft.CommonLanguageServerProtocol.Framework.ILspLogger>().Object);
        }

        private static Microsoft.CopilotStudio.McsCore.InMemoryFileAccessorFactory StandaloneWorkflowFactory(Microsoft.CopilotStudio.McsCore.DirectoryPath workspaceFolder)
        {
            var factory = new Microsoft.CopilotStudio.McsCore.InMemoryFileAccessorFactory();
            using var stream = factory.Create(workspaceFolder).OpenWrite(new Microsoft.CopilotStudio.McsCore.AgentFilePath(WorkflowWorkspace.ProjectFileName));
            using var writer = new StreamWriter(stream);

            writer.Write("workflowProjectVersion: 1\n");

            return factory;
        }
    }
}
