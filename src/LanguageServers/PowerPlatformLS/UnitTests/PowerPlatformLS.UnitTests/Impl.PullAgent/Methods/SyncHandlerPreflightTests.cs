namespace Microsoft.PowerPlatformLS.UnitTests.Impl.PullAgent.Methods
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.Agents.ObjectModel.FileProjection;
    using Microsoft.Agents.Platform.Content;
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.CopilotStudio.Sync;
    using Microsoft.CopilotStudio.Sync.Dataverse;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Models;
    using Microsoft.PowerPlatformLS.Impl.PullAgent;
    using Microsoft.PowerPlatformLS.Impl.PullAgent.Auth;
    using Microsoft.PowerPlatformLS.UnitTests.Impl.Language.CopilotStudio;
    using Moq;
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Xunit;

    public class SyncHandlerPreflightTests
    {
        private const string SkillAnchorPath = "behaviors/get-us-weather/skill.mcs.yml";
        private const string ManifestPath = "behaviors/get-us-weather/SKILL.md";
        private const string UnsupportedPath = "entities/Unsupported.mcs.yml";
        private const string ConflictedManifest = "---\nname: get-us-weather\n---\n<<<<<<< ours\nLocal instructions\n=======\nRemote instructions\n>>>>>>> theirs\n";
        private const string LiteralMarkerManifest = "---\nname: get-us-weather\n---\nA conflict starts with an indented marker:\n\n  <<<<<<< ours\n\nand ends with:\n\n  >>>>>>> theirs\n";

        [Fact]
        public async Task ConflictedSkillManifest_FailsBeforeAnyCloudWrite()
        {
            using var fixture = new PreflightFixture();
            fixture.Write(ManifestPath, ConflictedManifest);

            var response = await fixture.SyncAsync(push: true);

            Assert.Equal(400, response.Code);
            Assert.Contains(ManifestPath, response.Message, StringComparison.Ordinal);
            Assert.Contains(McsConflictMarkers.Message, response.Message, StringComparison.Ordinal);
            fixture.AssertNoCloudWrites();
            Assert.Equal(ConflictedManifest, fixture.Read(ManifestPath));
        }

        [Fact]
        public void ConflictedSkillManifest_IsNotCaughtByTheDocumentGateAlone()
        {
            using var fixture = new PreflightFixture();
            fixture.Write(ManifestPath, ConflictedManifest);

            Assert.Empty(fixture.Workspace.GetUnreadableDocuments());
            Assert.Throws<WorkspaceValidationException>(
                () => fixture.Synchronizer.ThrowIfWorkspaceInvalid(fixture.Workspace.FolderPath, fixture.Workspace.Definition));
        }

        [Fact]
        public async Task LiteralMarkerTextInManifest_ReachesTheCloudWrites()
        {
            using var fixture = new PreflightFixture();
            fixture.Write(ManifestPath, LiteralMarkerManifest);

            var response = await fixture.SyncAsync(push: true);

            Assert.Equal(200, response.Code);
            fixture.AssertPushCompleted();
        }

        [Fact]
        public async Task CleanWorkspace_ReachesTheCloudWrites()
        {
            using var fixture = new PreflightFixture();

            var response = await fixture.SyncAsync(push: true);

            Assert.Equal(200, response.Code);
            fixture.AssertPushCompleted();
        }

        [Theory]
        [InlineData(true, UnsupportedPath)]
        [InlineData(false, UnsupportedPath)]
        public async Task SemanticallyInvalidDocument_FailsBeforeAnyCloudWrite(bool push, string path)
        {
            using var fixture = new PreflightFixture();
            fixture.Write(path, "kind: UnknownElement\ncontent: local text\n");

            var response = await fixture.SyncAsync(push);

            Assert.Equal(400, response.Code);
            Assert.Contains(path, response.Message, StringComparison.Ordinal);
            fixture.AssertNoCloudWrites();
        }

        [Fact]
        public async Task RemovingASemanticallyInvalidDocument_ReachesTheCloudWrites()
        {
            using var fixture = new PreflightFixture();
            fixture.Write(UnsupportedPath, "kind: UnknownElement\ncontent: local text\n");
            Assert.Equal(400, (await fixture.SyncAsync(push: true)).Code);
            fixture.AssertNoCloudWrites();

            fixture.Delete(UnsupportedPath);

            Assert.Equal(200, (await fixture.SyncAsync(push: true)).Code);
            fixture.AssertPushCompleted();
        }

        private sealed class PreflightFixture : IDisposable
        {
            private readonly Mock<IWorkspaceSynchronizer> _synchronizer = new(MockBehavior.Strict);
            private readonly Mock<IIslandControlPlaneService> _island = new(MockBehavior.Strict);
            private readonly Mock<ISyncDataverseClient> _dataverse = new(MockBehavior.Strict);
            private readonly Mock<IOperationContextProvider> _operationProvider = new(MockBehavior.Strict);
            private readonly Mock<ILspLogger> _logger = new();
            private readonly SyncAgentRequest _request;
            private World _world;

            public PreflightFixture()
            {
                Root = Path.Combine(Path.GetTempPath(), "preflight-" + Guid.NewGuid().ToString("N"));
                CopyDirectory(Path.GetFullPath(Path.Combine("TestData", "Workspace", "NestedSkillWorkspace")), Root);

                Synchronizer = new WorkspaceSynchronizer(
                    new SyncMcsFileParser(LspProjectorService.Instance),
                    new FileAccessorFactory(),
                    Mock.Of<IIslandControlPlaneService>(),
                    Mock.Of<ISyncProgress>(),
                    new LspComponentPathResolver());

                _island.Setup(service => service.SetConnectionContext(It.IsAny<string>(), It.IsAny<CoreServicesClusterCategory>()));
                _dataverse.Setup(client => client.SetDataverseUrl(It.IsAny<string>()));
                _synchronizer.Setup(service => service.IsSyncInfoAvailable(It.IsAny<DirectoryPath>())).Returns(false);
                _synchronizer.Setup(service => service.GetSyncInfoAsync(It.IsAny<DirectoryPath>()))
                    .ReturnsAsync(new AgentSyncInfo { AgentId = Guid.NewGuid() });
                _synchronizer.Setup(service => service.ThrowIfWorkspaceInvalid(It.IsAny<DirectoryPath>(), It.IsAny<DefinitionBase>()))
                    .Callback((DirectoryPath folder, DefinitionBase definition) => Synchronizer.ThrowIfWorkspaceInvalid(folder, definition));
                _synchronizer.Setup(service => service.PushCustomConnectorsAsync(It.IsAny<DirectoryPath>(), It.IsAny<ISyncDataverseClient>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new CustomConnectorPushResult());
                _synchronizer.Setup(service => service.ProvisionConnectionReferencesAsync(It.IsAny<DirectoryPath>(), It.IsAny<DefinitionBase>(), It.IsAny<ISyncDataverseClient>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyDictionary<string, Guid>?>()))
                    .Returns(Task.CompletedTask);
                _synchronizer.Setup(service => service.UpsertWorkflowForAgentAsync(It.IsAny<DirectoryPath>(), It.IsAny<ISyncDataverseClient>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>(), It.IsAny<WorkflowActivationMode>()))
                    .ReturnsAsync((ImmutableArray<WorkflowResponse>.Empty, new CloudFlowMetadata()));
                _synchronizer.Setup(service => service.UpsertAIPromptsForAgentAsync(It.IsAny<DirectoryPath>(), It.IsAny<ISyncDataverseClient>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync((ImmutableArray<SyncDataverseClient.AIPromptResponse>.Empty, ImmutableArray<SyncDataverseClient.AIPromptMetadata>.Empty));
                _synchronizer.Setup(service => service.PushLocalChangesAsync(It.IsAny<DirectoryPath>(), It.IsAny<AuthoringOperationContextBase>(), It.IsAny<DefinitionBase>(), It.IsAny<ISyncDataverseClient>(), It.IsAny<AgentSyncInfo>(), It.IsAny<CloudFlowMetadata?>(), It.IsAny<ImmutableArray<SyncDataverseClient.AIPromptMetadata>>(), It.IsAny<CancellationToken>(), It.IsAny<AuthoringOperationContextBase?>()))
                    .Returns(Task.CompletedTask);
                _synchronizer.Setup(service => service.GetLocalChangesAsync(It.IsAny<DirectoryPath>(), It.IsAny<DefinitionBase>(), It.IsAny<ISyncDataverseClient>(), It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync((new PvaComponentChangeSet(Array.Empty<BotComponentChange>(), null, "token"), ImmutableArray<Change>.Empty));
                _synchronizer.Setup(service => service.PullExistingChangesAsync(It.IsAny<DirectoryPath>(), It.IsAny<AuthoringOperationContextBase>(), It.IsAny<DefinitionBase>(), It.IsAny<ISyncDataverseClient>(), It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<ICollection<WorkspaceDiagnostic>>()))
                    .ReturnsAsync(() => Workspace.Definition);
                _operationProvider.Setup(provider => provider.GetAsync(It.IsAny<AgentSyncInfo>()))
                    .ReturnsAsync(new AuthoringOperationContext(null, new CdsOrganizationInfo(), new BotReference(), null, false));

                _request = new SyncAgentRequest
                {
                    WorkspaceUri = new Uri(Root),
                    AccountInfo = new AccountInfo(),
                    EnvironmentInfo = new EnvironmentInfo
                    {
                        DataverseUrl = "https://test.crm.dynamics.com",
                        AgentManagementUrl = "https://test.agentmanagement.com",
                        EnvironmentId = "test-environment",
                        DisplayName = "Test environment",
                    },
                    SolutionVersions = new SolutionInfo(),
                    DataverseAccessToken = "test-dataverse-token",
                    CopilotStudioAccessToken = "test-copilot-token",
                };

                _world = new World(Root);
                Workspace.BuildCompilationModel();
            }

            public string Root { get; }

            public WorkspaceSynchronizer Synchronizer { get; }

            public McsWorkspace Workspace => _world.GetWorkspace();

            public string File(string relativePath) => Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));

            public string Read(string relativePath) => System.IO.File.ReadAllText(File(relativePath));

            public void Write(string relativePath, string content)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(File(relativePath))!);
                System.IO.File.WriteAllText(File(relativePath), content);
                _world = new World(Root);
                Workspace.BuildCompilationModel();
            }

            public void Delete(string relativePath)
            {
                System.IO.File.Delete(File(relativePath));
                _world = new World(Root);
                Workspace.BuildCompilationModel();
            }

            public Task<SyncAgentResponse> SyncAsync(bool push)
            {
                var httpAccessor = new LspDataverseHttpClientAccessor(Mock.Of<ISyncAuthProvider>());
                SyncHandler handler = push
                    ? new SyncPushHandler(_island.Object, _synchronizer.Object, new TestTokenManager(), _dataverse.Object, httpAccessor, _operationProvider.Object, _logger.Object)
                    : new SyncPullHandler(_island.Object, _synchronizer.Object, new TestTokenManager(), _dataverse.Object, httpAccessor, _operationProvider.Object, _world.GetRequiredService<IClientWorkspaceFileProvider>(), _logger.Object);

                var document = _world.GetDocument(File("settings.mcs.yml"))!;
                return handler.HandleRequestAsync(_request, _world.GetRequestContext(document, 0), CancellationToken.None);
            }

            public void AssertNoCloudWrites()
            {
                _synchronizer.Verify(service => service.PushCustomConnectorsAsync(It.IsAny<DirectoryPath>(), It.IsAny<ISyncDataverseClient>(), It.IsAny<CancellationToken>()), Times.Never);
                _synchronizer.Verify(service => service.ProvisionConnectionReferencesAsync(It.IsAny<DirectoryPath>(), It.IsAny<DefinitionBase>(), It.IsAny<ISyncDataverseClient>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyDictionary<string, Guid>?>()), Times.Never);
                _synchronizer.Verify(service => service.UpsertWorkflowForAgentAsync(It.IsAny<DirectoryPath>(), It.IsAny<ISyncDataverseClient>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>(), It.IsAny<WorkflowActivationMode>()), Times.Never);
                _synchronizer.Verify(service => service.UpsertAIPromptsForAgentAsync(It.IsAny<DirectoryPath>(), It.IsAny<ISyncDataverseClient>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
                _synchronizer.Verify(service => service.PushLocalChangesAsync(It.IsAny<DirectoryPath>(), It.IsAny<AuthoringOperationContextBase>(), It.IsAny<DefinitionBase>(), It.IsAny<ISyncDataverseClient>(), It.IsAny<AgentSyncInfo>(), It.IsAny<CloudFlowMetadata?>(), It.IsAny<ImmutableArray<SyncDataverseClient.AIPromptMetadata>>(), It.IsAny<CancellationToken>(), It.IsAny<AuthoringOperationContextBase?>()), Times.Never);
                _synchronizer.Verify(service => service.PullExistingChangesAsync(It.IsAny<DirectoryPath>(), It.IsAny<AuthoringOperationContextBase>(), It.IsAny<DefinitionBase>(), It.IsAny<ISyncDataverseClient>(), It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<ICollection<WorkspaceDiagnostic>>()), Times.Never);
            }

            public void AssertPushCompleted()
                => _synchronizer.Verify(service => service.PushLocalChangesAsync(It.IsAny<DirectoryPath>(), It.IsAny<AuthoringOperationContextBase>(), It.IsAny<DefinitionBase>(), It.IsAny<ISyncDataverseClient>(), It.IsAny<AgentSyncInfo>(), It.IsAny<CloudFlowMetadata?>(), It.IsAny<ImmutableArray<SyncDataverseClient.AIPromptMetadata>>(), It.IsAny<CancellationToken>(), It.IsAny<AuthoringOperationContextBase?>()), Times.Once);

            public void Dispose() => Directory.Delete(Root, recursive: true);

            private static void CopyDirectory(string source, string destination)
            {
                Directory.CreateDirectory(destination);
                foreach (var file in Directory.EnumerateFiles(source))
                {
                    System.IO.File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
                }

                foreach (var directory in Directory.EnumerateDirectories(source))
                {
                    CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
                }
            }
        }
    }
}
