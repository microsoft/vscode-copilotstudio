namespace Microsoft.PowerPlatformLS.UnitTests.Impl.PullAgent.Methods
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.Agents.Platform.Content;
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.CopilotStudio.Sync;
    using Microsoft.CopilotStudio.Sync.Dataverse;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Impl.PullAgent;
    using Microsoft.PowerPlatformLS.Impl.PullAgent.Auth;
    using Moq;
    using System;
    using System.Collections.Immutable;
    using System.IO;
    using System.Net;
    using System.Threading;
    using System.Threading.Tasks;
    using Xunit;

    public sealed class CloneAgentHandlerTests : IDisposable
    {
        private readonly string _rootFolder = Path.Combine(Path.GetTempPath(), $"clone-agent-handler-{Guid.NewGuid():N}");
        private readonly AgentInfo _agentInfo = new() { AgentId = Guid.NewGuid(), DisplayName = "Payroll Agent" };
        private readonly Mock<IWorkspaceSynchronizer> _synchronizer = new();
        private readonly Mock<ISyncDataverseClient> _dataverseClient = new();
        private readonly Mock<IOperationContextProvider> _operationContextProvider = new();

        public void Dispose()
        {
            if (Directory.Exists(_rootFolder))
            {
                Directory.Delete(_rootFolder, recursive: true);
            }
        }

        [Fact]
        public async Task HandleRequest_ManagedAgent_ReturnsBadRequestWithoutWritingAWorkspace()
        {
            SetupCloudAgent(isManaged: true);

            var response = await CreateHandler().HandleRequestAsync(CreateRequest(), default, CancellationToken.None);

            Assert.Equal(400, response.Code);
            Assert.Contains($"({_agentInfo.AgentId}) is managed", response.Message, StringComparison.Ordinal);
            Assert.False(Directory.Exists(_rootFolder));
            _operationContextProvider.VerifyNoOtherCalls();
            _synchronizer.Verify(synchronizer => synchronizer.SaveSyncInfoAsync(It.IsAny<DirectoryPath>(), It.IsAny<AgentSyncInfo>()), Times.Never);
            _synchronizer.Verify(synchronizer => synchronizer.CloneChangesAsync(It.IsAny<DirectoryPath>(), It.IsAny<ReferenceTracker>(), It.IsAny<AuthoringOperationContextBase>(), It.IsAny<ISyncDataverseClient>(), It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task HandleRequest_AgentLookupUnavailable_PreservesServiceStatus()
        {
            _dataverseClient
                .Setup(client => client.GetAgentInfoAsync(_agentInfo.AgentId, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new DataverseRequestException(HttpStatusCode.ServiceUnavailable, "Service unavailable"));

            var response = await CreateHandler().HandleRequestAsync(CreateRequest(), default, CancellationToken.None);

            Assert.Equal(503, response.Code);
            Assert.Contains("Service unavailable", response.Message, StringComparison.Ordinal);
            Assert.False(Directory.Exists(_rootFolder));
            _operationContextProvider.VerifyNoOtherCalls();
            _synchronizer.Verify(synchronizer => synchronizer.SaveSyncInfoAsync(It.IsAny<DirectoryPath>(), It.IsAny<AgentSyncInfo>()), Times.Never);
        }

        [Fact]
        public async Task HandleRequest_UnmanagedAgent_ChecksTheAgentThenClonesIt()
        {
            SetupCloudAgent(isManaged: false);
            _operationContextProvider
                .Setup(provider => provider.GetAllAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<AssetsToClone>()))
                .ReturnsAsync(ImmutableArray.Create<AuthoringOperationContextBase>(new AuthoringOperationContext(new PrincipalObjectReference(Guid.NewGuid(), Guid.Empty), new CdsOrganizationInfo(Guid.NewGuid(), new Uri("https://test.crm.dynamics.com"), pvaSolutionVersion: new Version(1, 0), dvTableSearchGlossaryAndSynonymsSolutionVersion: new Version(1, 0), dvTableSearchSolutionVersion: new Version(1, 0)), new BotReference("environment-id", _agentInfo.AgentId), null, false)));

            var response = await CreateHandler().HandleRequestAsync(CreateRequest(), default, CancellationToken.None);

            Assert.Equal(200, response.Code);
            Assert.Equal("Payroll Agent", response.AgentFolderName);
            _dataverseClient.Verify(client => client.GetAgentInfoAsync(_agentInfo.AgentId, It.IsAny<CancellationToken>()), Times.Once);
            _synchronizer.Verify(synchronizer => synchronizer.CloneChangesAsync(It.IsAny<DirectoryPath>(), It.IsAny<ReferenceTracker>(), It.IsAny<AuthoringOperationContext>(), _dataverseClient.Object, It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task HandleRequest_CollectionOnlyCloneOfAManagedAgent_ClonesTheCollectionWithoutLookingUpTheAgent()
        {
            SetupCloudAgent(isManaged: true);
            var collectionId = Guid.NewGuid();
            _agentInfo.ComponentCollections.Add(new ComponentCollectionInfo { Id = collectionId, DisplayName = "Payroll Topics" });
            _operationContextProvider
                .Setup(provider => provider.GetAllAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<AssetsToClone>()))
                .ReturnsAsync(ImmutableArray.Create<AuthoringOperationContextBase>(new BotComponentCollectionAuthoringOperationContext(impersonatedUser: null, organizationInfo: new CdsOrganizationInfo(), reference: new BotComponentCollectionReference(environmentId: "environment-id", cdsId: collectionId), solutionUniqueName: null)));
            var request = CreateRequest();
            request.Assets = new AssetsToClone { ComponentCollectionIds = [collectionId] };

            Assert.Equal(200, (await CreateHandler().HandleRequestAsync(request, default, CancellationToken.None)).Code);

            _dataverseClient.Verify(client => client.GetAgentInfoAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
            _synchronizer.Verify(synchronizer => synchronizer.CloneChangesAsync(It.IsAny<DirectoryPath>(), It.IsAny<ReferenceTracker>(), It.IsAny<BotComponentCollectionAuthoringOperationContext>(), _dataverseClient.Object, It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        private void SetupCloudAgent(bool isManaged)
            => _dataverseClient
                .Setup(client => client.GetAgentInfoAsync(_agentInfo.AgentId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AgentInfo { AgentId = _agentInfo.AgentId, DisplayName = _agentInfo.DisplayName, IsManaged = isManaged });

        private CloneAgentHandler CreateHandler()
        {
            var authProvider = new Mock<ISyncAuthProvider>();
            authProvider.Setup(provider => provider.AcquireTokenAsync(It.IsAny<Uri>(), It.IsAny<CancellationToken>())).ReturnsAsync("token");
            return new CloneAgentHandler(new Mock<IIslandControlPlaneService>().Object, _synchronizer.Object, new TestTokenManager(), _dataverseClient.Object, new LspDataverseHttpClientAccessor(authProvider.Object), _operationContextProvider.Object, new Mock<ILspLogger>().Object);
        }

        private CloneAgentRequest CreateRequest() => new()
        {
            AgentInfo = _agentInfo,
            Assets = new AssetsToClone { CloneAgent = true },
            RootFolder = new Uri(_rootFolder),
            AccountInfo = new AccountInfo { AccountId = "account-id", TenantId = Guid.NewGuid(), AccountEmail = "user@contoso.com" },
            EnvironmentInfo = new EnvironmentInfo { DataverseUrl = "https://test.crm.dynamics.com", AgentManagementUrl = "https://test.agentmanagement.com", EnvironmentId = "environment-id", DisplayName = "Test Environment" },
            SolutionVersions = new SolutionInfo { CopilotStudioSolutionVersion = new Version(1, 0, 0, 0) },
            CopilotStudioAccessToken = "copilot-studio-token",
            DataverseAccessToken = "dataverse-token",
        };
    }
}
