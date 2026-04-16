namespace Microsoft.PowerPlatformLS.UnitTests.Impl.PullAgent.Methods
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.Agents.Platform.Content;
    using Microsoft.Agents.Platform.Content.Exceptions;
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.CopilotStudio.Sync;
    using Microsoft.CopilotStudio.Sync.Dataverse;
    using Microsoft.PowerPlatformLS.Contracts.FileLayout;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Impl.PullAgent;
    using Microsoft.PowerPlatformLS.Impl.PullAgent.Auth;
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
    using static Microsoft.CopilotStudio.Sync.Dataverse.SyncDataverseClient;
    using Microsoft.CopilotStudio.McsCore;

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
            var handler = TestHandlerFactory.CreateHandler(new MockDataverseClient(), new TestWorkspaceSynchronizer(), CreateOperationProvider());

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
            var failingClient = new Mock<ISyncDataverseClient>();
            failingClient.Setup(c => c.GetAgentIdBySchemaNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(Guid.Empty);
            failingClient.Setup(c => c.CreateNewAgentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("Dataverse failure!"));
            var handler = TestHandlerFactory.CreateHandler(failingClient.Object, new TestWorkspaceSynchronizer(), CreateOperationProvider());

            var response = await handler.HandleRequestAsync(context.Request, context.RequestContext, CancellationToken.None);

            Assert.NotEqual(200, response.Code);
            Assert.Equal(Guid.Empty, response.AgentSyncInfo?.AgentId);
            Assert.NotNull(response.Message);
        }

        [Fact]
        public async Task ReattachAgentInvalidDirectoryTest()
        {
            var context = CreateTestSetup("Workspace/InvalidWorkspace");
            var handler = TestHandlerFactory.CreateHandler(new MockDataverseClient(), new TestWorkspaceSynchronizer(), CreateOperationProvider());

            var response = await handler.HandleRequestAsync(context.Request, context.RequestContext, CancellationToken.None);

            Assert.NotEqual(200, response.Code);
            Assert.Equal(Guid.Empty, response.AgentSyncInfo?.AgentId);
            Assert.NotNull(response.Message);
        }

        [Fact]
        public async Task ReattachAgentWithExistingConnJsonTest()
        {
            var context = CreateTestSetup();
            var handler = TestHandlerFactory.CreateHandler(new MockDataverseClient(), new TestWorkspaceSynchronizerSyncInfoExists(), CreateOperationProvider());

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
            var existingAgentClient = new MockDataverseClient();
            var mockClient = new Mock<ISyncDataverseClient>();
            mockClient.Setup(c => c.GetAgentIdBySchemaNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(Guid.NewGuid());
            var handler = TestHandlerFactory.CreateHandler(mockClient.Object, new TestWorkspaceSynchronizer(), CreateOperationProvider());

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
                new Microsoft.CopilotStudio.McsCore.DirectoryPath(WorkspacePath),
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
            var badRequestClient = new Mock<ISyncDataverseClient>();
            badRequestClient.Setup(c => c.GetAgentIdBySchemaNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new DataverseBadRequestException(
                    errorCodeName: "BadRequest",
                    errorCodeValue: "400",
                    serviceRequestId: Guid.NewGuid().ToString(),
                    message: "Invalid schema name",
                    innerException: null));
            var handler = TestHandlerFactory.CreateHandler(badRequestClient.Object, new TestWorkspaceSynchronizer(), CreateOperationProvider(), mockLogger.Object);

            var response = await handler.HandleRequestAsync(context.Request, context.RequestContext, CancellationToken.None);

            Assert.Equal(400, response.Code);
            Assert.Contains("BadRequest", response.Message);
            mockLogger.Verify(l => l.LogException(It.IsAny<DataverseBadRequestException>(), It.IsAny<string>(), It.IsAny<object[]>()), Times.Once);
        }

        [Fact]
        public async Task ReattachAgentWithExceptionTest()
        {
            var context = CreateTestSetup();
            var throwingClient = new Mock<ISyncDataverseClient>();
            throwingClient.Setup(c => c.GetAgentIdBySchemaNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Throws(new InvalidOperationException("invalid operation exception"));
            var handler = TestHandlerFactory.CreateHandler(throwingClient.Object, new TestWorkspaceSynchronizer(), CreateOperationProvider());

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
            var trackingClient = new MockDataverseClientWithConnectionTracking();
            var handler = TestHandlerFactory.CreateHandler(trackingClient, new TestWorkspaceSynchronizer(), CreateOperationProvider());

            var response = await handler.HandleRequestAsync(context.Request, context.RequestContext, CancellationToken.None);

            Assert.Equal(200, response.Code);
            // No connections should be provisioned since workspace has no portable connections
            Assert.Empty(trackingClient.ProvisionedConnections);
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
    internal class MockDataverseClient : ISyncDataverseClient
    {
        private WorkflowMetadata[]? _workflowsForAgent;

        public List<(Guid? AgentId, WorkflowMetadata Metadata, string Operation)> WorkflowCalls { get; } = new();
        
        private Dictionary<string, ConnectionReferenceInfo> _connectionReferencesByLogicalName = new(StringComparer.OrdinalIgnoreCase);

        public void SetDataverseUrl(string dataverseUrl) { }

        public void SetWorkflowsForAgent(WorkflowMetadata[] workflows)
        {
            _workflowsForAgent = workflows;
        }

        public virtual Task<AgentInfo> CreateNewAgentAsync(string newAgentName, string schemaName, CancellationToken cancellationToken)
        {
            var fakeAgent = new AgentInfo
            {
                AgentId = Guid.NewGuid(),
                DisplayName = newAgentName,
                IconBase64 = "icon"
            };
            return Task.FromResult(fakeAgent);
        }

        public virtual Task<Guid> GetAgentIdBySchemaNameAsync(string schemaName, CancellationToken cancellationToken)
        {
            return Task.FromResult(Guid.Empty);
        }

        public virtual Task<WorkflowMetadata[]> DownloadAllWorkflowsForAgentAsync(Guid? agentId, CancellationToken cancellationToken)
        {
            return Task.FromResult(_workflowsForAgent ?? Array.Empty<WorkflowMetadata>());
        }

        public virtual Task<WorkflowResponse> InsertWorkflowAsync(Guid? agentId, WorkflowMetadata? workflowMetadata, CancellationToken cancellationToken)
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

        public virtual Task<WorkflowResponse> UpdateWorkflowAsync(Guid? agentId, WorkflowMetadata? workflowMetadata, CancellationToken cancellationToken)
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

        public virtual Task<bool> ConnectionReferenceExistsAsync(string connectionReferenceLogicalName, CancellationToken cancellationToken)
            => Task.FromResult(false);

        public virtual Task CreateConnectionReferenceAsync(string connectionReferenceLogicalName, string connectorId, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public virtual Task EnsureConnectionReferenceExistsAsync(string connectionReferenceLogicalName, string connectorId, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public void SetConnectionReferences(ConnectionReferenceInfo[] connectionReferences)
        {
            _connectionReferencesByLogicalName = connectionReferences.ToDictionary(x => x.ConnectionReferenceLogicalName, StringComparer.OrdinalIgnoreCase);
        }

        public virtual Task<ConnectionReferenceInfo[]> GetConnectionReferencesByLogicalNamesAsync(IEnumerable<string> logicalNames, CancellationToken cancellationToken)
        {
            var result = logicalNames
                .Where(l => _connectionReferencesByLogicalName.ContainsKey(l))
                .Select(l => _connectionReferencesByLogicalName[l])
                .ToList();

            return Task.FromResult(result.ToArray());
        }

        public virtual Task<SolutionInfo> GetSolutionVersionsAsync(CancellationToken cancellationToken)
            => Task.FromResult(new SolutionInfo { CopilotStudioSolutionVersion = new Version(1, 0, 0, 0) });

        public virtual Task<AgentInfo> GetAgentInfoAsync(Guid agentId, CancellationToken cancellationToken)
            => Task.FromResult(new AgentInfo { AgentId = agentId });

        public virtual Task DownloadKnowledgeFileAsync(string knowledgeFileFolder, BotComponentId botComponentId, string fileName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public virtual Task UploadKnowledgeFileAsync(string knowledgeFileFolder, Guid botComponentId, string fileName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    internal static class TestHandlerFactory
    {
        public static ReattachAgentHandler CreateHandler(ISyncDataverseClient dataverseClient, IWorkspaceSynchronizer workspace, IOperationContextProvider opProvider, ILspLogger? logger = null)
        {
            var mockAuthProvider = new Mock<ISyncAuthProvider>();
            mockAuthProvider.Setup(a => a.AcquireTokenAsync(It.IsAny<Uri>(), It.IsAny<CancellationToken>())).ReturnsAsync("mock-token");
            var accessor = new LspDataverseHttpClientAccessor(mockAuthProvider.Object);
            return new ReattachAgentHandler(
                new Mock<IIslandControlPlaneService>().Object,
                workspace,
                new TestTokenManager(),
                dataverseClient,
                accessor,
                opProvider,
                logger ?? new Mock<ILspLogger>().Object);
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

        public virtual bool IsSyncInfoAvailable(Microsoft.CopilotStudio.McsCore.DirectoryPath workspaceFolder) => false;

        public Task<AgentSyncInfo> GetSyncInfoAsync(Microsoft.CopilotStudio.McsCore.DirectoryPath workspaceFolder)
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

        public Task SaveSyncInfoAsync(Microsoft.CopilotStudio.McsCore.DirectoryPath workspaceFolder, AgentSyncInfo connectionDetails)
        {
            return Task.CompletedTask;
        }

        public Task<(PvaComponentChangeSet, ImmutableArray<Change>)> GetLocalChangesAsync(Microsoft.CopilotStudio.McsCore.DirectoryPath workspaceFolder, DefinitionBase workspaceDefinition, ISyncDataverseClient dataverseClient, Guid? agentId, CancellationToken cancellationToken)
        {
            return Task.FromResult((new PvaComponentChangeSet(Enumerable.Empty<BotComponentChange>(), null, "token"), ImmutableArray<Change>.Empty));
        }

        public Task<(PvaComponentChangeSet, ImmutableArray<Change>)> GetRemoteChangesAsync(Microsoft.CopilotStudio.McsCore.DirectoryPath workspaceFolder, AuthoringOperationContextBase operationContext, ISyncDataverseClient dataverseClient, Guid? agentId, CancellationToken cancellationToken)
        {
            return Task.FromResult((new PvaComponentChangeSet(Enumerable.Empty<BotComponentChange>(), null, "token"), ImmutableArray<Change>.Empty));
        }

        public Task CloneChangesAsync(Microsoft.CopilotStudio.McsCore.DirectoryPath workspaceFolder, ReferenceTracker referenceTracker, AuthoringOperationContextBase operationContext, ISyncDataverseClient dataverseClient, Guid? agentId, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task ApplyTouchupsAsync(Microsoft.CopilotStudio.McsCore.DirectoryPath workspaceFolder, ReferenceTracker referenceTracker, CancellationToken cancellation)
            => Task.CompletedTask;

        public Task<DefinitionBase> PullExistingChangesAsync(Microsoft.CopilotStudio.McsCore.DirectoryPath workspaceFolder, AuthoringOperationContextBase operationContext, DefinitionBase localWorkspaceDefinition, ISyncDataverseClient dataverseClient, Guid? agentId, CancellationToken cancellationToken, bool downloadAllKnowledgeFiles = false)
            => Task.FromResult(localWorkspaceDefinition);

        public Task<int> PushChangesetAsync(Microsoft.CopilotStudio.McsCore.DirectoryPath workspaceFolder, AuthoringOperationContextBase operationContext, PvaComponentChangeSet localWorkspaceDefinition, ISyncDataverseClient dataverseClient, Guid? agentId, CloudFlowMetadata? cloudFlowMetadata, CancellationToken cancellationToken, bool uploadAllKnowledgeFiles = false)
            => Task.FromResult(0);

        public Task<WorkspaceSyncInfo> SyncWorkspaceAsync(Microsoft.CopilotStudio.McsCore.DirectoryPath workspaceFolder, AuthoringOperationContextBase operationContext, string? changeToken, bool updateWorkspaceDirectory, ISyncDataverseClient dataverseClient, Guid? agentId, CloudFlowMetadata? cloudFlowMetadata, CancellationToken cancellationToken)
        {
            ReattachCalled = true;
            return Task.FromResult(new WorkspaceSyncInfo
            {
                Changeset = new PvaComponentChangeSet(Enumerable.Empty<BotComponentChange>(), null, "token"),
                Definition = new BotDefinition()
            });
        }

        public virtual Task<(ImmutableArray<WorkflowResponse>, CloudFlowMetadata)> UpsertWorkflowForAgentAsync(Microsoft.CopilotStudio.McsCore.DirectoryPath workspaceFolder, ISyncDataverseClient dataverseClient, Guid? agentId, CancellationToken cancellationToken)
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

        public Task<CloudFlowMetadata> GetWorkflowsAsync(Microsoft.CopilotStudio.McsCore.DirectoryPath workspaceFolder, ISyncDataverseClient dataverseClient, Guid? agentId, Microsoft.CopilotStudio.Sync.IFileAccessor fileAccessor, CancellationToken cancellationToken)
        {
            return Task.FromResult(new CloudFlowMetadata
            {
                Workflows = ImmutableArray<CloudFlowDefinition>.Empty,
                ConnectionReferences = ImmutableArray<ConnectionReference>.Empty
            });
        }

        public Task ProvisionConnectionReferencesAsync(DefinitionBase definition, ISyncDataverseClient dataverseClient, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<DefinitionBase> ReadWorkspaceDefinitionAsync(Microsoft.CopilotStudio.McsCore.DirectoryPath workspaceFolder, CancellationToken cancellationToken, bool checkKnowledgeFiles = false)
            => Task.FromResult<DefinitionBase>(new BotDefinition());

        public Task<PushVerificationResult> VerifyPushAsync(Microsoft.CopilotStudio.McsCore.DirectoryPath workspaceFolder, AuthoringOperationContextBase operationContext, ISyncDataverseClient dataverseClient, Guid? agentId, CancellationToken cancellationToken)
            => Task.FromResult(new PushVerificationResult { IsFullyAccepted = true });

        public Task<ImmutableArray<Microsoft.CopilotStudio.McsCore.DirectoryPath>> CloneAllAssetsAsync(Microsoft.CopilotStudio.McsCore.DirectoryPath rootFolder, AgentSyncInfo syncInfo, AssetsToClone assetsToClone, AgentInfo agentInfo, IOperationContextProvider operationContextProvider, ISyncDataverseClient dataverseClient, CancellationToken cancellationToken)
            => Task.FromResult(ImmutableArray<Microsoft.CopilotStudio.McsCore.DirectoryPath>.Empty);
    }

    internal class TestWorkspaceSynchronizerSyncInfoExists : TestWorkspaceSynchronizer
    {
        public override bool IsSyncInfoAvailable(Microsoft.CopilotStudio.McsCore.DirectoryPath workspaceFolder) => true;
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

    internal class ReattachAgentTestContext
    {
        public required World World { get; set; }

        public required RequestContext RequestContext { get; set; }

        public required ReattachAgentRequest Request { get; set; }
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
}
