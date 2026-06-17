namespace Microsoft.PowerPlatformLS.UnitTests.Impl.PullAgent.Methods
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.Agents.Platform.Content.Exceptions;
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.CopilotStudio.Sync;
    using Microsoft.CopilotStudio.Sync.Dataverse;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Impl.PullAgent;
    using Microsoft.PowerPlatformLS.Impl.PullAgent.Auth;
    using Microsoft.PowerPlatformLS.UnitTests.Impl.Language.CopilotStudio;
    using Moq;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Xunit;
    using DirectoryPath = Microsoft.CopilotStudio.McsCore.DirectoryPath;

    public class PreparePushHandlerTests
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
        public async Task PreparePushValidWorkspaceReturns200Test()
        {
            var (requestContext, request) = CreateSetup();
            var handler = CreateHandler(new MockDataverseClient(), new TestWorkspaceSynchronizer());

            var response = await handler.HandleRequestAsync(request, requestContext, CancellationToken.None);

            Assert.Equal(200, response.Code);
            Assert.True(response.AgentConnections.IsDefaultOrEmpty);
        }

        [Fact]
        public async Task PreparePushReturnsProvisionedConnectionsTest()
        {
            var (requestContext, request) = CreateSetup();
            var connection = new ConnectionNeeded
            {
                ConnectionReferenceLogicalName = "cr_needed",
                ConnectorId = "connector-id",
                ConnectorName = "My Connector"
            };
            var handler = CreateHandler(new MockDataverseClient(), new PreparePushConnectionReturningSynchronizer(connection));

            var response = await handler.HandleRequestAsync(request, requestContext, CancellationToken.None);

            Assert.Equal(200, response.Code);
            var returned = Assert.Single(response.AgentConnections);
            Assert.Equal("cr_needed", returned.ConnectionReferenceLogicalName);
        }

        [Fact]
        public async Task PreparePush_NonCliTemplate_ProceedsAsClassic()
        {
            // Issue #292: a classic agent created from a non-default gallery template (the
            // fixture's template: sdkagent-1.0.0) has no native CLI evidence, so it is
            // Classic/Supported. The push gate allows it and prepare proceeds to provisioning -
            // the template is a template, not an authoring shape, and must not fail closed.
            var (requestContext, request) = CreateSetup("Workspace/UnrecognizedTemplateWorkspace");
            var synchronizer = new PreparePushProvisionTrackingSynchronizer();
            var handler = CreateHandler(new MockDataverseClient(), synchronizer);

            var response = await handler.HandleRequestAsync(request, requestContext, CancellationToken.None);

            Assert.Equal(200, response.Code);
            Assert.True(synchronizer.ProvisionAttempted);
        }

        [Fact]
        public async Task PreparePushBadRequestReturnsStatusCodeTest()
        {
            var (requestContext, request) = CreateSetup();
            var logger = new Mock<ILspLogger>();
            var synchronizer = new PreparePushThrowingSynchronizer(new DataverseBadRequestException(
                errorCodeName: "BadRequest",
                errorCodeValue: "400",
                serviceRequestId: Guid.NewGuid().ToString(),
                message: "Invalid connector",
                innerException: null));
            var handler = CreateHandler(new MockDataverseClient(), synchronizer, logger.Object);

            var response = await handler.HandleRequestAsync(request, requestContext, CancellationToken.None);

            Assert.Equal(400, response.Code);
            Assert.Contains("BadRequest", response.Message);
            logger.Verify(l => l.LogException(It.IsAny<DataverseBadRequestException>(), It.IsAny<string>(), It.IsAny<object[]>()), Times.Once);
        }

        [Fact]
        public async Task PreparePushGenericExceptionReturns500Test()
        {
            var (requestContext, request) = CreateSetup();
            var synchronizer = new PreparePushThrowingSynchronizer(new InvalidOperationException("provision failed"));
            var handler = CreateHandler(new MockDataverseClient(), synchronizer);

            var response = await handler.HandleRequestAsync(request, requestContext, CancellationToken.None);

            Assert.Equal(500, response.Code);
            Assert.Contains("provision failed", response.Message);
        }

        private static (RequestContext, PreparePushRequest) CreateSetup(string? customWorkspace = null)
        {
            var workspacePath = Path.GetFullPath(Path.Combine(TestDataPath, customWorkspace ?? WorkspacePath));
            var world = new World(workspacePath);
            var doc = world.GetDocument(Path.Combine(workspacePath, TopicsPath));
            var requestContext = world.GetRequestContext(doc, 0);

            var request = new PreparePushRequest
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
                DataverseAccessToken = DataverseToken
            };

            return (requestContext, request);
        }

        private static PreparePushHandler CreateHandler(ISyncDataverseClient dataverseClient, IWorkspaceSynchronizer synchronizer, ILspLogger? logger = null)
        {
            var mockAuthProvider = new Mock<ISyncAuthProvider>();
            mockAuthProvider.Setup(a => a.AcquireTokenAsync(It.IsAny<Uri>(), It.IsAny<CancellationToken>())).ReturnsAsync("mock-token");
            var accessor = new LspDataverseHttpClientAccessor(mockAuthProvider.Object);

            return new PreparePushHandler(
                new Mock<IIslandControlPlaneService>().Object,
                synchronizer,
                new TestTokenManager(),
                dataverseClient,
                accessor,
                logger ?? new Mock<ILspLogger>().Object);
        }
    }

    internal sealed class PreparePushConnectionReturningSynchronizer : TestWorkspaceSynchronizer
    {
        private readonly ConnectionNeeded _connection;

        public PreparePushConnectionReturningSynchronizer(ConnectionNeeded connection)
        {
            _connection = connection;
        }

        public override Task<IReadOnlyList<ConnectionNeeded>> GetAgentConnectionReferencesAsync(DirectoryPath workspaceFolder, DefinitionBase definition, ISyncDataverseClient dataverseClient, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ConnectionNeeded>>(new[] { _connection });
    }

    internal sealed class PreparePushProvisionTrackingSynchronizer : TestWorkspaceSynchronizer
    {
        public bool ProvisionAttempted { get; private set; }

        public override Task<CustomConnectorPushResult> PushCustomConnectorsAsync(DirectoryPath workspaceFolder, ISyncDataverseClient dataverseClient, CancellationToken cancellationToken)
        {
            ProvisionAttempted = true;
            return base.PushCustomConnectorsAsync(workspaceFolder, dataverseClient, cancellationToken);
        }
    }

    internal sealed class PreparePushThrowingSynchronizer : TestWorkspaceSynchronizer
    {
        private readonly Exception _exception;

        public PreparePushThrowingSynchronizer(Exception exception)
        {
            _exception = exception;
        }

        public override Task<CustomConnectorPushResult> PushCustomConnectorsAsync(DirectoryPath workspaceFolder, ISyncDataverseClient dataverseClient, CancellationToken cancellationToken)
            => throw _exception;
    }
}
