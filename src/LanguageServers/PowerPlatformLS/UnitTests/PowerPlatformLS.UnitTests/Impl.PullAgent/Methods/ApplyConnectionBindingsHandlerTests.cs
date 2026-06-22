namespace Microsoft.PowerPlatformLS.UnitTests.Impl.PullAgent.Methods
{
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.CopilotStudio.Sync;
    using Microsoft.CopilotStudio.Sync.Dataverse;
    using Microsoft.PowerPlatformLS.Contracts.Internal;
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

    public class ApplyConnectionBindingsHandlerTests
    {
        private const string TestDataPath = "TestData";
        private const string WorkspacePath = "Workspace/LocalWorkspace";
        private const string TopicsPath = "topics/Goodbye.mcs.yml";
        private const string EnvironmentId = "TestEnvironment";
        private const string AccountId = "testAccount";
        private const string AccountEmail = "testEmail";
        private const string DataverseUrl = "https://test.crm.dynamics.com";
        private const string AgentManagementUrl = "https://test.agentmanagement.com";
        private const string CopilotStudioToken = "CopilotStudioToken";
        private const string DataverseToken = "DataverseToken";

        [Fact]
        public async Task ApplyConnectionBindings_RepublishesDiagnosticsAfterBinding()
        {
            var workspacePath = Path.GetFullPath(Path.Combine(TestDataPath, WorkspacePath));
            var world = new World(workspacePath);
            var doc = world.GetDocument(Path.Combine(workspacePath, TopicsPath));
            Assert.NotNull(doc);
            var requestContext = world.GetRequestContext(doc!, 0);

            var diagnosticsPublisher = new Mock<IDiagnosticsPublisher>();
            var handler = CreateHandler(new TestWorkspaceSynchronizer(), diagnosticsPublisher.Object);

            var response = await handler.HandleRequestAsync(CreateRequest(workspacePath), requestContext, CancellationToken.None);

            Assert.Equal(200, response.Code);
            diagnosticsPublisher.Verify(
                p => p.PublishAllDiagnosticsAsync(It.IsAny<RequestContext>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task ApplyConnectionBindings_DiagnosticsRepublishFailure_DoesNotFailResponse()
        {
            var workspacePath = Path.GetFullPath(Path.Combine(TestDataPath, WorkspacePath));
            var world = new World(workspacePath);
            var doc = world.GetDocument(Path.Combine(workspacePath, TopicsPath));
            Assert.NotNull(doc);
            var requestContext = world.GetRequestContext(doc!, 0);

            var diagnosticsPublisher = new Mock<IDiagnosticsPublisher>();
            diagnosticsPublisher
                .Setup(p => p.PublishAllDiagnosticsAsync(It.IsAny<RequestContext>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("publish failed"));
            var handler = CreateHandler(new TestWorkspaceSynchronizer(), diagnosticsPublisher.Object);

            var response = await handler.HandleRequestAsync(CreateRequest(workspacePath), requestContext, CancellationToken.None);

            Assert.Equal(200, response.Code);
        }

        private static ApplyConnectionBindingsRequest CreateRequest(string workspacePath) => new()
        {
            WorkspaceUri = new Uri(workspacePath),
            AccountInfo = new AccountInfo
            {
                AccountId = AccountId,
                TenantId = Guid.NewGuid(),
                AccountEmail = AccountEmail
            },
            EnvironmentInfo = new EnvironmentInfo
            {
                DataverseUrl = DataverseUrl,
                AgentManagementUrl = AgentManagementUrl,
                EnvironmentId = EnvironmentId,
                DisplayName = "Test Environment"
            },
            SolutionVersions = new SolutionInfo
            {
                CopilotStudioSolutionVersion = new Version(1, 0, 0, 0)
            },
            CopilotStudioAccessToken = CopilotStudioToken,
            DataverseAccessToken = DataverseToken,
        };

        private static ApplyConnectionBindingsHandler CreateHandler(IConnectionManagementService synchronizer, IDiagnosticsPublisher diagnosticsPublisher)
        {
            var mockAuthProvider = new Mock<ISyncAuthProvider>();
            mockAuthProvider
                .Setup(a => a.AcquireTokenAsync(It.IsAny<Uri>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("mock-token");
            var accessor = new LspDataverseHttpClientAccessor(mockAuthProvider.Object);

            return new ApplyConnectionBindingsHandler(
                new Mock<IIslandControlPlaneService>().Object,
                synchronizer,
                new TestTokenManager(),
                new MockDataverseClient(),
                new Mock<IConnectionCatalogClient>().Object,
                accessor,
                diagnosticsPublisher,
                new Mock<ILspLogger>().Object);
        }
    }
}
