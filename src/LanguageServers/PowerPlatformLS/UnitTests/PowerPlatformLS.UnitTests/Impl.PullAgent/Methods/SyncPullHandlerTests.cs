namespace Microsoft.PowerPlatformLS.UnitTests.Impl.PullAgent.Methods
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.Agents.ObjectModel.FileProjection;
    using Microsoft.Agents.Platform.Content;
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.CopilotStudio.Sync;
    using Microsoft.CopilotStudio.Sync.Dataverse;
    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Models;
    using Microsoft.PowerPlatformLS.Impl.PullAgent;
    using Microsoft.PowerPlatformLS.Impl.PullAgent.Auth;
    using Microsoft.PowerPlatformLS.UnitTests.Impl.Language.CopilotStudio;
    using Moq;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Xunit;

    public class SyncPullHandlerTests
    {
        private const string SettingsPath = "settings.mcs.yml";
        private const string TopicPath = "topics/Goodbye.mcs.yml";
        private const string Settings = "schemaName: test_agent\ndisplayName: Base\ntemplate: default-1.0.0\n";
        private const string Topic = "kind: AdaptiveDialog\nbeginDialog:\n  kind: OnRecognizedIntent\n  id: main\n";
        private const string Conflict = "<<<<<<< ours\ndisplayName: Local\n=======\ndisplayName: Remote\n>>>>>>> theirs\n";
        private static readonly Guid PersistedTenantId = Guid.Empty;
        private static readonly Guid SelectedTenantId = Guid.Parse("22222222-2222-2222-2222-222222222222");

        public static IEnumerable<object[]> InvalidDocuments()
        {
            foreach (var (path, text, message) in new[]
            {
                (SettingsPath, "displayName: Local\ndisplayName: Other\n", "Duplicate key"),
                (SettingsPath, "configuration:\n\trecognizer: invalid\n", "Tab"),
                (SettingsPath, "displayName: \"unterminated\n", "quoted"),
                (SettingsPath, Conflict, "Unresolved merge conflict"),
                (TopicPath, "kind: AdaptiveDialog\nkind: AdaptiveDialog\n", "Duplicate key"),
                (TopicPath, "kind: AdaptiveDialog\nbeginDialog:\n\tkind: OnRecognizedIntent\n", "Tab"),
                (TopicPath, "kind: AdaptiveDialog\nmodelDescription: \"unterminated\n", "quoted"),
                (TopicPath, "kind: AdaptiveDialog\n" + Conflict, "Unresolved merge conflict"),
            })
            {
                yield return new object[] { path, text, message, true };
                yield return new object[] { path, text, message, false };
            }
        }

        [Theory]
        [MemberData(nameof(InvalidDocuments))]
        public async Task Pull_UnreadableLocalYaml_DoesNotStartSyncOrChangeFiles(string path, string text, string message, bool saved)
        {
            using var fixture = new PullFixture();
            fixture.Edit(path, text, saved);
            var before = fixture.Snapshot();

            var response = await fixture.PullAsync();

            Assert.Equal(400, response.Code);
            Assert.Contains(path, response.Message, StringComparison.Ordinal);
            Assert.Contains(message, response.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(response.LocalChanges);
            Assert.Equal(text, fixture.World.GetDocument(fixture.File(path))!.Text);
            fixture.AssertUnchanged(before);
            fixture.AssertNoSyncCalls();
        }

        [Fact]
        public async Task Pull_MultipleUnreadableFiles_ReportsBothWithoutStartingSync()
        {
            using var fixture = new PullFixture();
            fixture.Edit(SettingsPath, Conflict);
            fixture.Edit(TopicPath, "kind: AdaptiveDialog\nkind: AdaptiveDialog\n");
            var before = fixture.Snapshot();

            var response = await fixture.PullAsync();

            Assert.Equal(400, response.Code);
            Assert.Contains("2 workspace files", response.Message, StringComparison.Ordinal);
            Assert.Contains(SettingsPath, response.Message, StringComparison.Ordinal);
            Assert.Contains(TopicPath, response.Message, StringComparison.Ordinal);
            fixture.AssertUnchanged(before);
            fixture.AssertNoSyncCalls();
        }

        [Fact]
        public async Task Pull_ValidWorkspaceWithNoRemoteChanges_Succeeds()
        {
            using var fixture = new PullFixture();

            var response = await fixture.PullAsync();

            Assert.Equal(200, response.Code);
            Assert.Equal(string.Empty, response.Message);
            fixture.AssertPullCalledOnce();
        }

        [Fact]
        public async Task Pull_RepairsEmptyPersistedTenantInMemoryWithoutChangingPersistedWorkspaceIdentity()
        {
            using var fixture = new PullFixture();

            var response = await fixture.PullAsync();

            Assert.Equal(200, response.Code);
            fixture.AssertRequestConnectionContextUsed();
        }

        [Fact]
        public async Task Pull_ValidLocalAndRemoteEdits_CanCreateConflictsAndWarn()
        {
            using var fixture = new PullFixture();
            fixture.Edit(SettingsPath, Settings.Replace("Base", "Local"));
            fixture.SetRemoteName("Remote");

            var response = await fixture.PullAsync();

            Assert.Equal(200, response.Code);
            Assert.Contains("1 workspace file has unresolved merge conflicts", response.Message, StringComparison.Ordinal);
            Assert.Contains(SettingsPath, response.Message, StringComparison.Ordinal);
            var merged = System.IO.File.ReadAllText(fixture.File(SettingsPath));
            Assert.Contains("<<<<<<<", merged, StringComparison.Ordinal);
            Assert.Contains("Local", merged, StringComparison.Ordinal);
            Assert.Contains("Remote", merged, StringComparison.Ordinal);
            var cache = System.IO.File.ReadAllText(fixture.File(".mcs/botdefinition.json"));
            Assert.Contains("Remote", cache, StringComparison.Ordinal);
            Assert.DoesNotContain("Local", cache, StringComparison.Ordinal);
            Assert.DoesNotContain("<<<<<<<", cache, StringComparison.Ordinal);
            fixture.AssertPullCalledOnce();

            fixture.Edit(SettingsPath, merged, saved: false);
            var before = fixture.Snapshot();
            var blocked = await fixture.PullAsync();

            Assert.Equal(400, blocked.Code);
            fixture.AssertPullCalledOnce();
            fixture.AssertUnchanged(before);
        }

        [Fact]
        public async Task Pull_RepairingTheDocument_UnblocksPull()
        {
            using var fixture = new PullFixture();
            fixture.Edit(TopicPath, "kind: AdaptiveDialog\n" + Conflict);
            Assert.Equal(400, (await fixture.PullAsync()).Code);
            fixture.AssertNoSyncCalls();
            fixture.Edit(TopicPath, Topic);

            Assert.Equal(200, (await fixture.PullAsync()).Code);
            fixture.AssertPullCalledOnce();
        }

        [Fact]
        public async Task Pull_DeletingTheInvalidTopic_UnblocksPullWithoutRestoringIt()
        {
            using var fixture = new PullFixture();
            fixture.Edit(TopicPath, "kind: AdaptiveDialog\n" + Conflict);
            Assert.Equal(400, (await fixture.PullAsync()).Code);
            fixture.AssertNoSyncCalls();
            System.IO.File.Delete(fixture.File(TopicPath));
            fixture.Workspace.RemoveDocument(new FilePath(fixture.File(TopicPath).Replace('\\', '/')));

            var response = await fixture.PullAsync();

            Assert.Equal(200, response.Code);
            Assert.False(System.IO.File.Exists(fixture.File(TopicPath)));
            Assert.Equal(ChangeType.Delete, Assert.Single(response.LocalChanges.Where(change => change.Uri == TopicPath)).ChangeType);
            fixture.AssertPullCalledOnce();
        }

        private sealed class PullFixture : IDisposable
        {
            private readonly Mock<IWorkspaceSynchronizer> _synchronizer = new();
            private readonly Mock<IIslandControlPlaneService> _island = new();
            private readonly Mock<ISyncDataverseClient> _dataverse = new();
            private readonly Mock<IOperationContextProvider> _operationProvider = new();
            private readonly Mock<ILspLogger> _logger = new();
            private readonly SyncPullHandler _handler;
            private readonly SyncAgentRequest _request;
            private readonly BotEntity _entity;
            private readonly AgentSyncInfo _persistedSyncInfo;
            private AgentSyncInfo? _operationSyncInfo;
            private AgentSyncInfo? _pullSyncInfo;
            private AgentSyncInfo? _localChangesSyncInfo;

            public PullFixture()
            {
                Root = Path.Combine(Path.GetTempPath(), "pull-preflight-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path.Combine(Root, "topics"));
                var factory = new FileAccessorFactory();
                var accessor = factory.Create(new DirectoryPath(Root.Replace('\\', '/')));
                var entityBuilder = CodeSerializer.Deserialize<BotEntity>(Settings)!.ToBuilder();
                entityBuilder.Version = 1;
                _entity = entityBuilder.Build();
                var topic = new DialogComponent(
                    schemaName: "test_agent.topic.Goodbye",
                    displayName: "Goodbye",
                    description: string.Empty,
                    id: Guid.NewGuid(),
                    parentBotComponentId: default,
                    dialog: CodeSerializer.Deserialize<AdaptiveDialog>(Topic)!);
                WorkspaceSynchronizer.WriteCloudCache(accessor, new BotDefinition().WithEntity(_entity).WithComponents(new[] { topic }));
                System.IO.File.WriteAllText(File(SettingsPath), Settings);
                System.IO.File.WriteAllText(File(TopicPath), Topic);
                System.IO.File.WriteAllText(File(".mcs/changetoken.txt"), "before-pull");
                World = new World(Root);
                Workspace = World.GetWorkspace();
                Workspace.BuildCompilationModel();

                _persistedSyncInfo = new AgentSyncInfo
                {
                    AgentId = Guid.NewGuid(),
                    DataverseEndpoint = new Uri("https://persisted.crm.dynamics.com"),
                    EnvironmentId = "persisted-environment",
                    EnvironmentDisplayName = "Persisted environment",
                    AccountInfo = new AccountInfo
                    {
                        AccountId = "persisted-account",
                        AccountEmail = "persisted@example.com",
                        TenantId = PersistedTenantId,
                    },
                    SolutionVersions = new SolutionInfo(),
                    AgentManagementEndpoint = new Uri("https://persisted.agentmanagement.com"),
                    AuthoringShape = AuthoringShape.CliCopilot,
                };
                var operation = new AuthoringOperationContext(null, new CdsOrganizationInfo(), new BotReference(), null, false);
                var sync = new WorkspaceSynchronizer(new SyncMcsFileParser(LspProjectorService.Instance), factory, _island.Object, Mock.Of<ISyncProgress>(), new LspComponentPathResolver());
                _island.Setup(value => value.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new PvaComponentChangeSet(Array.Empty<BotComponentChange>(), _entity, "after-pull"));
                _dataverse.Setup(value => value.DownloadAllWorkflowsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(Array.Empty<SyncDataverseClient.WorkflowMetadata>());
                _dataverse.Setup(value => value.DownloadAllAIPromptsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(Array.Empty<SyncDataverseClient.AIPromptMetadata>());
                _operationProvider.Setup(value => value.GetAsync(It.IsAny<AgentSyncInfo>()))
                    .Callback<AgentSyncInfo>(info => _operationSyncInfo = info)
                    .ReturnsAsync(operation);
                _synchronizer.Setup(value => value.GetSyncInfoAsync(It.IsAny<DirectoryPath>())).ReturnsAsync(_persistedSyncInfo);
                _synchronizer.Setup(value => value.PullExistingChangesAsync(
                    It.IsAny<DirectoryPath>(), It.IsAny<AuthoringOperationContextBase>(), It.IsAny<DefinitionBase>(),
                    It.IsAny<ISyncDataverseClient>(), It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>(),
                    It.IsAny<bool>(), It.IsAny<ICollection<WorkspaceDiagnostic>>()))
                    .Returns((DirectoryPath folder, AuthoringOperationContextBase context, DefinitionBase definition, ISyncDataverseClient client, AgentSyncInfo info, CancellationToken token, bool downloadAll, ICollection<WorkspaceDiagnostic> conflicts)
                        =>
                        {
                            _pullSyncInfo = info;
                            return sync.PullExistingChangesAsync(folder, context, definition, client, info, token, downloadAll, conflicts);
                        });
                _synchronizer.Setup(value => value.GetLocalChangesAsync(
                    It.IsAny<DirectoryPath>(), It.IsAny<DefinitionBase>(), It.IsAny<ISyncDataverseClient>(), It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>()))
                    .Returns((DirectoryPath folder, DefinitionBase definition, ISyncDataverseClient client, AgentSyncInfo info, CancellationToken token)
                        =>
                        {
                            _localChangesSyncInfo = info;
                            return sync.GetLocalChangesAsync(folder, definition, client, info, token);
                        });
                _request = new SyncAgentRequest
                {
                    WorkspaceUri = new Uri(Root),
                    AccountInfo = new AccountInfo
                    {
                        AccountId = "selected-account",
                        AccountEmail = "selected@example.com",
                        TenantId = SelectedTenantId,
                    },
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
                _handler = new SyncPullHandler(
                    _island.Object, _synchronizer.Object, new TestTokenManager(), _dataverse.Object,
                    new LspDataverseHttpClientAccessor(Mock.Of<ISyncAuthProvider>()), _operationProvider.Object,
                    World.GetRequiredService<IClientWorkspaceFileProvider>(), _logger.Object);
            }

            public string Root { get; }
            public World World { get; }
            public McsWorkspace Workspace { get; }

            public string File(string relativePath) => Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));

            public void Edit(string relativePath, string text, bool saved = true)
            {
                if (saved)
                {
                    System.IO.File.WriteAllText(File(relativePath), text);
                }
                World.GetDocument(File(relativePath))!.UpdateText(text);
                Workspace.BuildCompilationModel();
            }

            public void SetRemoteName(string name)
            {
                var builder = _entity.ToBuilder();
                builder.DisplayName = name;
                builder.Version = 2;
                _island.Setup(value => value.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new PvaComponentChangeSet(Array.Empty<BotComponentChange>(), builder.Build(), "after-pull"));
            }

            public Dictionary<string, byte[]> Snapshot()
                => Directory.GetFiles(Root, "*", SearchOption.AllDirectories).ToDictionary(path => path, System.IO.File.ReadAllBytes);

            public void AssertUnchanged(Dictionary<string, byte[]> before)
            {
                Assert.Equal(before.Keys.OrderBy(path => path), Directory.GetFiles(Root, "*", SearchOption.AllDirectories).OrderBy(path => path));
                foreach (var entry in before)
                {
                    Assert.Equal(entry.Value, System.IO.File.ReadAllBytes(entry.Key));
                }
            }

            public void AssertNoSyncCalls()
            {
                _synchronizer.VerifyNoOtherCalls();
                _island.VerifyNoOtherCalls();
                _dataverse.VerifyNoOtherCalls();
                _operationProvider.VerifyNoOtherCalls();
                _logger.VerifyNoOtherCalls();
            }

            public void AssertPullCalledOnce()
                => _synchronizer.Verify(value => value.PullExistingChangesAsync(
                    It.IsAny<DirectoryPath>(), It.IsAny<AuthoringOperationContextBase>(), It.IsAny<DefinitionBase>(),
                    It.IsAny<ISyncDataverseClient>(), It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>(),
                    false, It.IsAny<ICollection<WorkspaceDiagnostic>>()), Times.Once);

            public void AssertRequestConnectionContextUsed()
            {
                AssertEffective(_operationSyncInfo);
                Assert.Same(_operationSyncInfo, _pullSyncInfo);
                Assert.Same(_operationSyncInfo, _localChangesSyncInfo);

                Assert.Equal("persisted-account", _persistedSyncInfo.AccountInfo?.AccountId);
                Assert.Equal(PersistedTenantId, _persistedSyncInfo.AccountInfo?.TenantId);
                Assert.Equal("persisted-environment", _persistedSyncInfo.EnvironmentId);
                Assert.Equal(new Uri("https://persisted.agentmanagement.com"), _persistedSyncInfo.AgentManagementEndpoint);
            }

            private void AssertEffective(AgentSyncInfo? info)
            {
                Assert.NotNull(info);
                Assert.Equal(_persistedSyncInfo.AgentId, info.AgentId);
                Assert.Equal(_persistedSyncInfo.ComponentCollectionId, info.ComponentCollectionId);
                Assert.Equal(_persistedSyncInfo.EnvironmentDisplayName, info.EnvironmentDisplayName);
                Assert.Equal(_persistedSyncInfo.AuthoringShape, info.AuthoringShape);
                Assert.Equal("selected-account", info.AccountInfo?.AccountId);
                Assert.Equal("selected@example.com", info.AccountInfo?.AccountEmail);
                Assert.Equal(SelectedTenantId, info.AccountInfo?.TenantId);
                Assert.Equal(new Uri("https://test.crm.dynamics.com"), info.DataverseEndpoint);
                Assert.Equal("test-environment", info.EnvironmentId);
                Assert.Equal(new Uri("https://test.agentmanagement.com"), info.AgentManagementEndpoint);
                Assert.Same(_request.SolutionVersions, info.SolutionVersions);
            }

            public Task<SyncAgentResponse> PullAsync()
                => _handler.HandleRequestAsync(_request, World.GetRequestContext(World.GetDocument(File(SettingsPath))!, 0), CancellationToken.None);

            public void Dispose() => Directory.Delete(Root, recursive: true);
        }
    }
}
