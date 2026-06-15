// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.PowerPlatformLS.UnitTests.Impl.PullAgent.Methods
{
    using Microsoft.Agents.Platform.Content.Exceptions;
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.CopilotStudio.Sync;
    using Microsoft.CopilotStudio.Sync.Dataverse;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Impl.PullAgent;
    using Microsoft.PowerPlatformLS.UnitTests.Impl.Language.CopilotStudio;
    using Moq;
    using System;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Xunit;

    public class PrepareReattachHandlerTests
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
        public async Task PrepareReattachValidDirectoryTest()
        {
            var context = CreatePrepareTestSetup();
            var synchronizer = new TestWorkspaceSynchronizer();
            var handler = TestHandlerFactory.CreatePrepareHandler(new MockDataverseClient(), synchronizer);

            var response = await handler.HandleRequestAsync(context.Request, context.RequestContext, CancellationToken.None);

            Assert.Equal(200, response.Code);
            Assert.NotNull(response.AgentSyncInfo);
            Assert.Equal(EnvironmentId, response.AgentSyncInfo!.EnvironmentId);
            Assert.Equal(AccountId, response.AgentSyncInfo?.AccountInfo?.AccountId);
            Assert.True(response.IsNewAgent);
            Assert.Equal(0, synchronizer.SavedSyncInfoCount);
        }

        [Fact]
        public async Task PrepareReattachCreateAgentFailureTest()
        {
            var context = CreatePrepareTestSetup();
            var failingClient = new Mock<ISyncDataverseClient>();
            failingClient.Setup(c => c.GetAgentIdBySchemaNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(Guid.Empty);
            failingClient.Setup(c => c.CreateNewAgentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<AuthoringShape>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("Dataverse failure!"));
            var handler = TestHandlerFactory.CreatePrepareHandler(failingClient.Object, new TestWorkspaceSynchronizer());

            var response = await handler.HandleRequestAsync(context.Request, context.RequestContext, CancellationToken.None);

            Assert.NotEqual(200, response.Code);
            Assert.Equal(Guid.Empty, response.AgentSyncInfo?.AgentId);
            Assert.NotNull(response.Message);
        }

        [Fact]
        public async Task PrepareReattachInvalidDirectoryTest()
        {
            var context = CreatePrepareTestSetup("Workspace/InvalidWorkspace");
            var handler = TestHandlerFactory.CreatePrepareHandler(new MockDataverseClient(), new TestWorkspaceSynchronizer());

            var response = await handler.HandleRequestAsync(context.Request, context.RequestContext, CancellationToken.None);

            Assert.NotEqual(200, response.Code);
            Assert.Equal(Guid.Empty, response.AgentSyncInfo?.AgentId);
            Assert.NotNull(response.Message);
        }

        [Fact]
        public async Task PrepareReattachAlreadyConnectedTest()
        {
            var context = CreatePrepareTestSetup();
            var handler = TestHandlerFactory.CreatePrepareHandler(new MockDataverseClient(), new TestWorkspaceSynchronizerSyncInfoExists());

            var response = await handler.HandleRequestAsync(context.Request, context.RequestContext, CancellationToken.None);

            Assert.Equal(400, response.Code);
            Assert.Equal(Guid.Empty, response.AgentSyncInfo?.AgentId);
            Assert.NotNull(response.Message);
            Assert.False(response.IsNewAgent);
        }

        [Fact]
        public async Task PrepareReattachAlreadyConnectedMessageDoesNotLeakUrlTest()
        {
            var context = CreatePrepareTestSetup();
            var handler = TestHandlerFactory.CreatePrepareHandler(new MockDataverseClient(), new TestWorkspaceSynchronizerSyncInfoExists());

            var response = await handler.HandleRequestAsync(context.Request, context.RequestContext, CancellationToken.None);

            Assert.Equal(400, response.Code);
            Assert.DoesNotContain(DataverseUrl, response.Message);
            Assert.DoesNotContain(AgentManagementUrl, response.Message);
        }

        [Fact]
        public async Task PrepareReattachRemoteAgentExistsTest()
        {
            var context = CreatePrepareTestSetup();
            var mockClient = new Mock<ISyncDataverseClient>();
            mockClient.Setup(c => c.GetAgentIdBySchemaNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(Guid.NewGuid());
            var handler = TestHandlerFactory.CreatePrepareHandler(mockClient.Object, new TestWorkspaceSynchronizer());

            var response = await handler.HandleRequestAsync(context.Request, context.RequestContext, CancellationToken.None);

            Assert.Equal(200, response.Code);
            Assert.False(response.IsNewAgent);
        }

        [Fact]
        public async Task PrepareReattachReturnsAgentConnectionsTest()
        {
            var context = CreatePrepareTestSetup();
            var handler = TestHandlerFactory.CreatePrepareHandler(new MockDataverseClient(), new TestWorkspaceSynchronizer());

            var response = await handler.HandleRequestAsync(context.Request, context.RequestContext, CancellationToken.None);

            Assert.Equal(200, response.Code);
            Assert.True(response.AgentConnections.IsDefaultOrEmpty);
        }

        [Fact]
        public async Task PrepareReattachDataverseBadRequestTest()
        {
            var context = CreatePrepareTestSetup();
            var mockLogger = new Mock<ILspLogger>();
            var badRequestClient = new Mock<ISyncDataverseClient>();
            badRequestClient.Setup(c => c.GetAgentIdBySchemaNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new DataverseBadRequestException(
                    errorCodeName: "BadRequest",
                    errorCodeValue: "400",
                    serviceRequestId: Guid.NewGuid().ToString(),
                    message: "Invalid schema name",
                    innerException: null));
            var handler = TestHandlerFactory.CreatePrepareHandler(badRequestClient.Object, new TestWorkspaceSynchronizer(), mockLogger.Object);

            var response = await handler.HandleRequestAsync(context.Request, context.RequestContext, CancellationToken.None);

            Assert.Equal(400, response.Code);
            Assert.Contains("BadRequest", response.Message);
            mockLogger.Verify(l => l.LogException(It.IsAny<DataverseBadRequestException>(), It.IsAny<string>(), It.IsAny<object[]>()), Times.Once);
        }

        [Fact]
        public async Task PrepareReattachWithExceptionTest()
        {
            var context = CreatePrepareTestSetup();
            var throwingClient = new Mock<ISyncDataverseClient>();
            throwingClient.Setup(c => c.GetAgentIdBySchemaNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Throws(new InvalidOperationException("invalid operation exception"));
            var handler = TestHandlerFactory.CreatePrepareHandler(throwingClient.Object, new TestWorkspaceSynchronizer());

            var response = await handler.HandleRequestAsync(context.Request, context.RequestContext, CancellationToken.None);

            Assert.Equal(500, response.Code);
            Assert.Contains("exception", response.Message);
        }

        [Fact]
        public async Task PrepareReattachWithConnectionTrackingDoesNotFailTest()
        {
            var context = CreatePrepareTestSetup();
            var trackingClient = new MockDataverseClientWithConnectionTracking();
            var handler = TestHandlerFactory.CreatePrepareHandler(trackingClient, new TestWorkspaceSynchronizer());

            var response = await handler.HandleRequestAsync(context.Request, context.RequestContext, CancellationToken.None);

            Assert.Equal(200, response.Code);
            Assert.Empty(trackingClient.ProvisionedConnections);
        }

        [Fact]
        public async Task PrepareReattach_UnrecognizedTemplate_BlockedWith400_NoMutationTest()
        {
            var context = CreatePrepareTestSetup("Workspace/UnrecognizedTemplateWorkspace");
            var dataverse = new Mock<ISyncDataverseClient>();
            dataverse.Setup(c => c.GetAgentIdBySchemaNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(Guid.Empty);
            var handler = TestHandlerFactory.CreatePrepareHandler(dataverse.Object, new TestWorkspaceSynchronizer());

            var response = await handler.HandleRequestAsync(context.Request, context.RequestContext, CancellationToken.None);

            Assert.Equal(400, response.Code);
            Assert.False(response.IsNewAgent);
            Assert.Equal(Guid.Empty, response.AgentSyncInfo?.AgentId);
            Assert.Contains(SyncOperation.Reattach.ToString(), response.Message);
            dataverse.Verify(c => c.GetAgentIdBySchemaNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
            dataverse.Verify(c => c.CreateNewAgentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<AuthoringShape>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task PrepareReattach_ComponentCollectionRoot_NotBlockedByGateTest()
        {
            var dir = Path.GetFullPath(Path.Combine(TestDataPath, "WorkspaceWithCC"));
            var world = new World(dir);
            var ccDir = Path.Combine(dir, "MyCC333");
            var workspace = world.GetWorkspace(ccDir);
            var doc = workspace.GetDocumentOrThrow(new AgentFilePath("collection.mcs.yml"));
            var requestContext = world.GetRequestContext(doc, 0);

            var request = new PrepareReattachRequest
            {
                WorkspaceUri = new Uri(ccDir),
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

            var handler = TestHandlerFactory.CreatePrepareHandler(new MockDataverseClient(), new TestWorkspaceSynchronizer());

            var response = await handler.HandleRequestAsync(request, requestContext, CancellationToken.None);

            Assert.Equal(200, response.Code);
            Assert.True(response.IsNewAgent);
        }

        private PrepareReattachTestContext CreatePrepareTestSetup(string? customWorkspace = null)
        {
            var workspacePath = Path.GetFullPath(Path.Combine(TestDataPath, customWorkspace ?? WorkspacePath));
            World world;
            RequestContext requestContext;

            if (Directory.Exists(workspacePath) && File.Exists(Path.Combine(workspacePath, TopicsPath)))
            {
                world = new World(workspacePath);
                var path = Path.Combine(workspacePath, TopicsPath);
                var doc = world.GetDocument(path);
                requestContext = world.GetRequestContext(doc, 0);
            }
            else
            {
                world = new World();
                var doc = world.AddFile("topic2.mcs.yml");
                requestContext = world.GetRequestContext(doc, 0);
            }

            var request = new PrepareReattachRequest
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

            return new PrepareReattachTestContext
            {
                World = world,
                RequestContext = requestContext,
                Request = request
            };
        }
    }

    internal class PrepareReattachTestContext
    {
        public required World World { get; init; }

        public required RequestContext RequestContext { get; init; }

        public required PrepareReattachRequest Request { get; init; }
    }
}
