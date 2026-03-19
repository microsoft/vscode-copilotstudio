namespace Microsoft.PowerPlatformLS.UnitTests.Impl.PullAgent.Methods
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.Agents.Platform.Content;
    using Microsoft.Agents.Platform.Content.Exceptions;
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.PowerPlatformLS.Contracts.FileLayout;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Impl.PullAgent;
    using Microsoft.PowerPlatformLS.Impl.PullAgent.Auth;
    using Microsoft.PowerPlatformLS.Impl.PullAgent.Dataverse;
    using Microsoft.PowerPlatformLS.UnitTests.Impl.Language.CopilotStudio;
    using Moq;
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.IO;
    using System.Linq;
    using System.Net.Http;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Xunit;
    using static Microsoft.PowerPlatformLS.Impl.PullAgent.Dataverse.DataverseClient;

    public class ReattachAgentHandlerTests
    {
        private const string TestDataPath = "TestData";
        private const string WorkspacePath = "Workspace/LocalWorkspace";
        private const string TopicsPath = "topics/Goodbye.mcs.yml";
        private const string EnvironmentId = "TestEnvironment";
        private const string AccountId = "testAccount";
        private const string AccountEmail = "testEmail";
        private const string DataverseUrl = "https://test.crm.dynamics.com";
        private const string AgentManagementUrl = "https://test.agentmanagement.com";
        private const string SolutionName = "TestSolution";
        private const string CopilotStudioToken = "CopilotStudioToken";
        private const string DataverseToken = "DataverseToken";

        [Fact]
        public async Task ReattachAgentValidDirectoryTest()
        {
            var context = CreateTestSetup();

            var handler = new TestReattachAgentHandler(
                new Mock<IIslandControlPlaneService>().Object,
                new TestWorkspaceSynchronizer(),
                new TestTokenManager(),
                (url, token) => new MockDataverseClient(),
                CreateOperationProvider(),
                new Mock<ILspLogger>().Object
            );

            var response = await handler.HandleRequestAsync(context.Request, context.RequestContext, CancellationToken.None);

            Assert.Equal(200, response.Code);
            Assert.NotNull(response.AgentSyncInfo);
            Assert.Equal(EnvironmentId, response.AgentSyncInfo!.EnvironmentId);
            Assert.Equal(AccountId, response.AgentSyncInfo?.AccountInfo?.AccountId);
            Assert.True(response.IsNewAgent);
        }

        [Fact]
        public async Task ReattachAgentDataverseFailureTest()
        {
            var context = CreateTestSetup();

            var handler = new FailingDataverseReattachHandler(
                new Mock<IIslandControlPlaneService>().Object,
                new TestWorkspaceSynchronizer(),
                new TestTokenManager(),
                (url, token) => new MockDataverseClient(),
                CreateOperationProvider(),
                new Mock<ILspLogger>().Object
            );

            var response = await handler.HandleRequestAsync(context.Request, context.RequestContext, CancellationToken.None);

            Assert.NotEqual(200, response.Code);
            Assert.Equal(Guid.Empty, response.AgentSyncInfo?.AgentId);
            Assert.NotNull(response.Message);
        }

        [Fact]
        public async Task ReattachAgentInvalidDirectoryTest()
        {
            var context = CreateTestSetup("Workspace/InvalidWorkspace");

            var handler = new TestReattachAgentHandler(
                new Mock<IIslandControlPlaneService>().Object,
                new TestWorkspaceSynchronizer(),
                new TestTokenManager(),
                (url, token) => new MockDataverseClient(),
                CreateOperationProvider(),
                new Mock<ILspLogger>().Object
            );

            var response = await handler.HandleRequestAsync(context.Request, context.RequestContext, CancellationToken.None);

            Assert.NotEqual(200, response.Code);
            Assert.Equal(Guid.Empty, response.AgentSyncInfo?.AgentId);
            Assert.NotNull(response.Message);
        }

        [Fact]
        public async Task ReattachAgentWithExistingConnJsonTest()
        {
            var context = CreateTestSetup();

            var handler = new TestReattachAgentHandler(
                new Mock<IIslandControlPlaneService>().Object,
                new TestWorkspaceSynchronizerSyncInfoExists(),
                new TestTokenManager(),
                (url, token) => new MockDataverseClient(),
                CreateOperationProvider(),
                new Mock<ILspLogger>().Object
            );

            var response = await handler.HandleRequestAsync(context.Request, context.RequestContext, CancellationToken.None);

            Assert.Equal(400, response.Code);
            Assert.Equal(Guid.Empty, response.AgentSyncInfo?.AgentId);
            Assert.NotNull(response.Message);
            Assert.False(response.IsNewAgent);
        }

        [Fact]
        public async Task ReattachAgentRemoteAgentExistsTest()
        {
            var context = CreateTestSetup();

            var handler = new TestReattachAgentHandlerExistingAgent(
                new Mock<IIslandControlPlaneService>().Object,
                new TestWorkspaceSynchronizer(),
                new TestTokenManager(),
                (url, token) => new MockDataverseClient(),
                CreateOperationProvider(),
                new Mock<ILspLogger>().Object
            );

            var response = await handler.HandleRequestAsync(context.Request, context.RequestContext, CancellationToken.None);

            Assert.Equal(200, response.Code);
            Assert.False(response.IsNewAgent);
            Assert.NotNull(response.Message);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task SyncWorkspaceAsyncTest(bool updateWorkspaceDirectory)
        {
            var context = CreateTestSetup();

            var synchronizer = new TestWorkspaceSynchronizer();

            var handler = new TestReattachAgentHandler(
                new Mock<IIslandControlPlaneService>().Object,
                synchronizer,
                new TestTokenManager(),
                (url, token) => new MockDataverseClient(),
                CreateOperationProvider(),
                new Mock<ILspLogger>().Object
            );

            var operationContext = await CreateOperationProvider().GetAsync(new AgentSyncInfo
            {
                AgentId = Guid.NewGuid(),
                EnvironmentId = EnvironmentId,
                DataverseEndpoint = new Uri(DataverseUrl),
                AgentManagementEndpoint = new Uri(AgentManagementUrl),
                AccountInfo = new AccountInfo
                {
                    AccountId = AccountId,
                    TenantId = Guid.NewGuid(),
                    AccountEmail = AccountEmail
                },
                SolutionVersions = new SolutionInfo
                {
                    CopilotStudioSolutionVersion = new Version(1, 0, 0, 0)
                },
            });

            var syncInfo = await synchronizer.SyncWorkspaceAsync(
                new DirectoryPath(WorkspacePath),
                operationContext,
                changeToken: "token",
                updateWorkspaceDirectory: updateWorkspaceDirectory,
                new MockDataverseClient(),
                Guid.NewGuid(),
                null,
                cancellationToken: CancellationToken.None
            );

            Assert.NotNull(syncInfo);
            Assert.NotNull(syncInfo.Definition);
            Assert.NotNull(syncInfo.Changeset);
            Assert.True(synchronizer.ReattachCalled);
        }

        [Fact]
        public async Task ReattachAgentDataverseBadRequestTest()
        {
            var context = CreateTestSetup();

            var mockLogger = new Mock<ILspLogger>();
            var handler = new TestReattachAgentHandlerDataverseBadRequest(
                new Mock<IIslandControlPlaneService>().Object,
                new TestWorkspaceSynchronizer(),
                new TestTokenManager(),
                (url, token) => new MockDataverseClient(),
                CreateOperationProvider(),
                mockLogger.Object
            );

            var response = await handler.HandleRequestAsync(context.Request, context.RequestContext, CancellationToken.None);

            Assert.Equal(400, response.Code);
            Assert.Contains("BadRequest", response.Message);
            mockLogger.Verify(l => l.LogException(It.IsAny<DataverseBadRequestException>(), It.IsAny<string>(), It.IsAny<object[]>()), Times.Once);
        }

        [Fact]
        public async Task ReattachAgentWithExceptionTest()
        {
            var context = CreateTestSetup();

            var handler = new TestReattachAgentHandlerWithException(
                new Mock<IIslandControlPlaneService>().Object,
                new TestWorkspaceSynchronizer(),
                new TestTokenManager(),
                (url, token) => new MockDataverseClient(),
                CreateOperationProvider(),
                new Mock<ILspLogger>().Object
            );

            var response = await handler.HandleRequestAsync(context.Request, context.RequestContext, CancellationToken.None);

            Assert.Equal(500, response.Code);
            Assert.Contains("exception", response.Message);
        }

        [Fact]
        public async Task ReattachAgentWithConnectionTrackingDoesNotFailTest()
        {
            // This test verifies that connection provisioning logic doesn't break the reattach flow
            // when there are no portable connections (workspace has no configuration)
            var context = CreateTestSetup();

            var handler = new TestReattachAgentHandlerWithConnectionTracking(
                new Mock<IIslandControlPlaneService>().Object,
                new TestWorkspaceSynchronizer(),
                new TestTokenManager(),
                (url, token) => new MockDataverseClient(),
                CreateOperationProvider(),
                new Mock<ILspLogger>().Object
            );

            var response = await handler.HandleRequestAsync(context.Request, context.RequestContext, CancellationToken.None);

            Assert.Equal(200, response.Code);
            // No connections should be provisioned since workspace has no portable connections
            Assert.Empty(handler.MockClient.ProvisionedConnections);
        }

        private ReattachAgentTestContext CreateTestSetup(string? customWorkspace = null)
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

            var request = new ReattachAgentRequest
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

            return new ReattachAgentTestContext
            {
                World = world,
                RequestContext = requestContext,
                Request = request
            };
        }

        private static IOperationContextProvider CreateOperationProvider()
        {
            var orgInfo = new CdsOrganizationInfo(
                tenantId: Guid.NewGuid(),
                cdsEndpoint: new Uri(DataverseUrl),
                pvaSolutionVersion: new Version(1, 0, 0, 0),
                dvTableSearchGlossaryAndSynonymsSolutionVersion: new Version(1, 0, 0, 0),
                dvTableSearchSolutionVersion: new Version(1, 0, 0, 0)
            );

            var reference = new BotComponentCollectionReference(
                environmentId: EnvironmentId,
                cdsId: Guid.NewGuid()
            );

            return new TestOperationContextProvider(
                new BotComponentCollectionAuthoringOperationContext(
                    impersonatedUser: null,
                    organizationInfo: orgInfo,
                    reference: reference,
                    solutionUniqueName: SolutionName
                )
            );
        }


    }

    /// <summary>
    /// Create mock Dataverse client that simulates agent creation
    /// </summary>
    internal class MockDataverseClient : DataverseClient
    {
        private WorkflowMetadata[]? _workflowsForAgent;

        public List<(Guid? AgentId, WorkflowMetadata Metadata, string Operation)> WorkflowCalls { get; } = new();

        public MockDataverseClient() : base(new HttpClient(), "https://test.crm.dynamics.com", "access-token", "MCSVSCode-1.0.0") { }

        public void SetWorkflowsForAgent(WorkflowMetadata[] workflows)
        {
            _workflowsForAgent = workflows;
        }

        public override Task<AgentInfo> CreateNewAgentAsync(string newAgentName, string schemaName, CancellationToken cancellationToken)
        {
            var fakeAgent = new AgentInfo
            {
                AgentId = Guid.NewGuid(),
                DisplayName = newAgentName,
                IconBase64 = "icon"
            };
            return Task.FromResult(fakeAgent);
        }

        public override Task<Guid> GetAgentIdBySchemaNameAsync(string schemaName, CancellationToken cancellationToken)
        {
            return Task.FromResult(Guid.Empty);
        }

        public override Task<WorkflowMetadata[]> DownloadAllWorkflowsForAgentAsync(Guid? agentId, CancellationToken cancellationToken)
        {
            return Task.FromResult(_workflowsForAgent ?? Array.Empty<WorkflowMetadata>());
        }

        public override Task<WorkflowResponse> InsertWorkflowAsync(Guid? agentId, WorkflowMetadata? workflowMetadata, CancellationToken cancellationToken)
        {
            if (workflowMetadata != null)
            {
                WorkflowCalls.Add((agentId, workflowMetadata, "Insert"));
            }
            return Task.FromResult(new WorkflowResponse
            {
                IsDisabled = false
            });
        }

        public override Task<WorkflowResponse> UpdateWorkflowAsync(Guid? agentId, WorkflowMetadata? workflowMetadata, CancellationToken cancellationToken)
        {
            if (workflowMetadata != null)
            {
                WorkflowCalls.Add((agentId, workflowMetadata, "Update"));
            }
            return Task.FromResult(new WorkflowResponse
            {
                IsDisabled = false
            });
        }
    }

    /// <summary>
    /// Dataverse client that throws instead of creating agents
    /// </summary>
    internal class FailingDataverseClient : DataverseClient
    {
        public FailingDataverseClient() : base(new HttpClient(), "https://fail.crm.dynamics.com", "access-token", "MCSVSCode-1.0.0") { }

        public override Task<AgentInfo> CreateNewAgentAsync(string newAgentName, string schemaName, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Dataverse failure!");
    }

    internal class FailingDataverseReattachHandler : ReattachAgentHandler
    {
        private readonly FailingDataverseClient _client = new();

        public FailingDataverseReattachHandler(
            IIslandControlPlaneService island,
            IWorkspaceSynchronizer workspace,
            ITokenManager tokenManager,
            Func<string, string, DataverseClient> dataverseClientFactory,
            IOperationContextProvider opProvider,
            ILspLogger logger)
            : base(island, workspace, tokenManager, dataverseClientFactory, opProvider, logger)
        { }

        protected override DataverseClient CreateDataverseClient(string dataverseUrl, string token) => _client;
    }

    internal class TestReattachAgentHandler : ReattachAgentHandler
    {
        private readonly MockDataverseClient _mockDataverseClient = new();

        public TestReattachAgentHandler(
            IIslandControlPlaneService island,
            IWorkspaceSynchronizer workspace,
            ITokenManager tokenManager,
            Func<string, string, DataverseClient> dataverseClientFactory,
            IOperationContextProvider opProvider,
            ILspLogger logger)
            : base(island, workspace, tokenManager, dataverseClientFactory, opProvider, logger)
        { }

        protected override DataverseClient CreateDataverseClient(string dataverseUrl, string token)
        {
            return _mockDataverseClient;
        }
    }

    internal class TestTokenManager : ITokenManager
    {
        public void SetTokens(string dataverseToken, string copilotStudioToken)
        {
            // No ops
        }
    }

    internal class TestWorkspaceSynchronizer : IWorkspaceSynchronizer
    {
        public bool ReattachCalled { get; private set; } = false;

        public virtual bool IsSyncInfoAvailable(DirectoryPath workspaceFolder) => false;

        public Task<AgentSyncInfo> GetSyncInfoAsync(DirectoryPath workspaceFolder)
        {
            return Task.FromResult(new AgentSyncInfo
            {
                AgentId = Guid.NewGuid(),
                EnvironmentId = "TestEnv",
                DataverseEndpoint = new Uri("https://test.crm.dynamics.com"),
                AgentManagementEndpoint = new Uri("https://test.agentmanagement.com"),
                AccountInfo = new AccountInfo
                {
                    AccountId = "testAccount",
                    TenantId = Guid.NewGuid(),
                    AccountEmail = "test@test.com"
                },
                SolutionVersions = new SolutionInfo
                {
                    CopilotStudioSolutionVersion = new Version(1, 0, 0, 0)
                },
            });
        }

        public Task SaveSyncInfoAsync(DirectoryPath workspaceFolder, AgentSyncInfo connectionDetails)
        {
            return Task.CompletedTask;
        }

        public Task<(PvaComponentChangeSet, ImmutableArray<Change>)> GetLocalChangesAsync(DirectoryPath workspaceFolder, DefinitionBase workspaceDefinition, DataverseClient dataverseClient, Guid? agentId, CancellationToken cancellationToken)
        {
            return Task.FromResult((new PvaComponentChangeSet(Enumerable.Empty<BotComponentChange>(), null, "token"), ImmutableArray<Change>.Empty));
        }

        public Task<(PvaComponentChangeSet, ImmutableArray<Change>)> GetRemoteChangesAsync(DirectoryPath workspaceFolder, AuthoringOperationContextBase operationContext, DataverseClient dataverseClient, Guid? agentId, CancellationToken cancellationToken)
        {
            return Task.FromResult((new PvaComponentChangeSet(Enumerable.Empty<BotComponentChange>(), null, "token"), ImmutableArray<Change>.Empty));
        }

        public Task CloneChangesAsync(DirectoryPath workspaceFolder, ReferenceTracker referenceTracker, AuthoringOperationContextBase operationContext, DataverseClient dataverseClient, Guid? agentId, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task ApplyTouchupsAsync(DirectoryPath workspaceFolder, ReferenceTracker referenceTracker, CancellationToken cancellation)
            => Task.CompletedTask;

        public Task<DefinitionBase> PullExistingChangesAsync(DirectoryPath workspaceFolder, AuthoringOperationContextBase operationContext, DefinitionBase localWorkspaceDefinition, DataverseClient dataverseClient, Guid? agentId, CancellationToken cancellationToken)
            => Task.FromResult(localWorkspaceDefinition);

        public Task PushChangesetAsync(DirectoryPath workspaceFolder, AuthoringOperationContextBase operationContext, PvaComponentChangeSet localWorkspaceDefinition, DataverseClient dataverseClient, Guid? agentId, CloudFlowMetadata? cloudFlowMetadata, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<WorkspaceSyncInfo> SyncWorkspaceAsync(DirectoryPath workspaceFolder, AuthoringOperationContextBase operationContext, string? changeToken, bool updateWorkspaceDirectory, DataverseClient dataverseClient, Guid? agentId, CloudFlowMetadata? cloudFlowMetadata, CancellationToken cancellationToken)
        {
            ReattachCalled = true;
            return Task.FromResult(new WorkspaceSyncInfo
            {
                Changeset = new PvaComponentChangeSet(Enumerable.Empty<BotComponentChange>(), null, "token"),
                Definition = new BotDefinition()
            });
        }

        public virtual Task<(ImmutableArray<WorkflowResponse>, CloudFlowMetadata)> UpsertWorkflowForAgentAsync(DirectoryPath workspaceFolder, DataverseClient dataverseClient, Guid? agentId, CancellationToken cancellationToken)
        {
            var emptyMetadata = new CloudFlowMetadata
            {
                Workflows = ImmutableArray<CloudFlowDefinition>.Empty,
                ConnectionReferences = ImmutableArray<ConnectionReference>.Empty
            };

            return Task.FromResult<(ImmutableArray<WorkflowResponse>, CloudFlowMetadata)>(
                (ImmutableArray<WorkflowResponse>.Empty, emptyMetadata)
            );
        }

        public Task<CloudFlowMetadata> GetWorkflowsAsync(DirectoryPath workspaceFolder, DataverseClient dataverseClient, Guid? agentId, IFileAccessor fileAccessor, CancellationToken cancellationToken)
        {
            return Task.FromResult(new CloudFlowMetadata
            {
                Workflows = ImmutableArray<CloudFlowDefinition>.Empty,
                ConnectionReferences = ImmutableArray<ConnectionReference>.Empty
            });
        }

        public Task ProvisionConnectionReferencesAsync(DefinitionBase definition, DataverseClient dataverseClient, CancellationToken cancellationToken)
            => Task.CompletedTask;        
    }

    internal class TestWorkspaceSynchronizerSyncInfoExists : TestWorkspaceSynchronizer
    {
        public override bool IsSyncInfoAvailable(DirectoryPath workspaceFolder) => true;
    }

    internal class TestOperationContextProvider : IOperationContextProvider
    {
        private readonly BotComponentCollectionAuthoringOperationContext _context;

        public TestOperationContextProvider(BotComponentCollectionAuthoringOperationContext context)
        {
            _context = context;
        }

        public Task<ImmutableArray<AuthoringOperationContextBase>> GetAllAsync(AgentSyncInfo agentInfo, AssetsToClone assetsToClone)
        {
            return Task.FromResult(ImmutableArray<AuthoringOperationContextBase>.Empty);
        }

        public Task<AuthoringOperationContextBase> GetAsync(AgentSyncInfo syncInfo)
        {
            return Task.FromResult<AuthoringOperationContextBase>(_context);
        }
    }

    internal class TestReattachAgentHandlerExistingAgent : ReattachAgentHandler
    {
        private class MockDataverseClientWithExistingAgent : MockDataverseClient
        {
            public override Task<Guid> GetAgentIdBySchemaNameAsync(string schemaName, CancellationToken cancellationToken)
            {
                return Task.FromResult(Guid.NewGuid());
            }
        }

        private readonly MockDataverseClientWithExistingAgent _mockDataverseClient = new();

        public TestReattachAgentHandlerExistingAgent(
            IIslandControlPlaneService island,
            IWorkspaceSynchronizer workspace,
            ITokenManager tokenManager,
            Func<string, string, DataverseClient> dataverseClientFactory,
            IOperationContextProvider opProvider,
            ILspLogger logger)
            : base(island, workspace, tokenManager, dataverseClientFactory, opProvider, logger)
        { }

        protected override DataverseClient CreateDataverseClient(string dataverseUrl, string token)
        {
            return _mockDataverseClient;
        }
    }

    internal class ReattachAgentTestContext
    {
        public required World World { get; set; }

        public required RequestContext RequestContext { get; set; }

        public required ReattachAgentRequest Request { get; set; }
    }

    internal class TestReattachAgentHandlerDataverseBadRequest : TestReattachAgentHandler
    {
        private class BadRequestClient : MockDataverseClient
        {
            public override Task<Guid> GetAgentIdBySchemaNameAsync(string schemaName, CancellationToken cancellationToken)
                => throw new DataverseBadRequestException(
                    errorCodeName: "BadRequest",
                    errorCodeValue: "400",
                    serviceRequestId: Guid.NewGuid().ToString(),
                    message: $"Invalid schema name: {schemaName}",
                    innerException: null
                );
        }

        private readonly BadRequestClient _client = new();

        public TestReattachAgentHandlerDataverseBadRequest(
            IIslandControlPlaneService island,
            IWorkspaceSynchronizer workspace,
            ITokenManager tokenManager,
            Func<string, string, DataverseClient> dataverseClientFactory,
            IOperationContextProvider opProvider,
            ILspLogger logger) : base(island, workspace, tokenManager, dataverseClientFactory, opProvider, logger) { }

        protected override DataverseClient CreateDataverseClient(string dataverseUrl, string token) => _client;
    }

    internal class TestReattachAgentHandlerWithException : TestReattachAgentHandler
    {
        public TestReattachAgentHandlerWithException(
            IIslandControlPlaneService island,
            IWorkspaceSynchronizer workspace,
            ITokenManager tokenManager,
            Func<string, string, DataverseClient> dataverseClientFactory,
            IOperationContextProvider opProvider,
            ILspLogger logger
        ) : base(island, workspace, tokenManager, dataverseClientFactory, opProvider, logger) { }

        protected override DataverseClient CreateDataverseClient(string dataverseUrl, string token)
        {
            throw new InvalidOperationException("invalid operation exception");
        }
    }

    internal class MockDataverseClientWithConnectionTracking : MockDataverseClient
    {
        public List<(string name, string connectorId)> ProvisionedConnections { get; } = new();

        public override Task<bool> ConnectionReferenceExistsAsync(string connectionReferenceLogicalName, CancellationToken cancellationToken)
        {
            return Task.FromResult(false); // Always return false to trigger creation
        }

        public override Task CreateConnectionReferenceAsync(string connectionReferenceLogicalName, string connectorId, CancellationToken cancellationToken)
        {
            ProvisionedConnections.Add((connectionReferenceLogicalName, connectorId));
            return Task.CompletedTask;
        }

        public override Task EnsureConnectionReferenceExistsAsync(string connectionReferenceLogicalName, string connectorId, CancellationToken cancellationToken)
        {
            ProvisionedConnections.Add((connectionReferenceLogicalName, connectorId));
            return Task.CompletedTask;
        }
    }

    internal class TestReattachAgentHandlerWithConnectionTracking : ReattachAgentHandler
    {
        private readonly MockDataverseClientWithConnectionTracking _mockClient = new();

        public MockDataverseClientWithConnectionTracking MockClient => _mockClient;

        public TestReattachAgentHandlerWithConnectionTracking(
            IIslandControlPlaneService island,
            IWorkspaceSynchronizer workspace,
            ITokenManager tokenManager,
            Func<string, string, DataverseClient> dataverseClientFactory,
            IOperationContextProvider opProvider,
            ILspLogger logger)
            : base(island, workspace, tokenManager, dataverseClientFactory, opProvider, logger)
        { }

        protected override DataverseClient CreateDataverseClient(string dataverseUrl, string token)
        {
            return _mockClient;
        }
    }
}
