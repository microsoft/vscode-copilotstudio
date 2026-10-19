namespace Microsoft.PowerPlatformLS.UnitTests.Impl.PullAgent.Methods
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.Agents.ObjectModel.FileProjection;
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.CopilotStudio.Sync;
    using IMcsWorkspace = Microsoft.PowerPlatformLS.Contracts.FileLayout.IMcsWorkspace;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Models;
    using Microsoft.PowerPlatformLS.Impl.PullAgent;
    using Microsoft.PowerPlatformLS.UnitTests.Impl.Language.CopilotStudio;
    using Moq;
    using System;
    using System.Collections.Immutable;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Xunit;

    public class GetLocalChangeHandlerTests
    {
        private const string TopicSchema = "test_agent.topic.Goodbye";
        private const string TopicPath = "topics/Goodbye.mcs.yml";
        private const string CleanTopic = "kind: AdaptiveDialog\nbeginDialog:\n  kind: OnRecognizedIntent\n  id: main\n  intent:\n    triggerQueries:\n      - goodbye\n";
        private const string ConflictedTopic = "kind: AdaptiveDialog\n<<<<<<< ours\nbeginDialog:\n  kind: OnRecognizedIntent\n=======\nbeginDialog:\n  kind: OnUnknownIntent\n>>>>>>> theirs\n";

        [Theory]
        [InlineData(ConflictedTopic, true)]
        [InlineData(ConflictedTopic, false)]
        [InlineData("kind: AdaptiveDialog\nkind: AdaptiveDialog\n", true)]
        [InlineData("kind: AdaptiveDialog\nkind: AdaptiveDialog\n", false)]
        [InlineData("kind: AdaptiveDialog\nbeginDialog:\n\tkind: OnRecognizedIntent\n", true)]
        [InlineData("kind: AdaptiveDialog\nbeginDialog:\n\tkind: OnRecognizedIntent\n", false)]
        [InlineData("kind: AdaptiveDialog\nmodelDescription: \"unterminated\n", true)]
        public async Task UnreadableExistingTopic_IsDisplayedAsModified(string text, bool saved)
        {
            using var fixture = new WorkspaceFixture();
            fixture.SetTopic(text, saved);
            Assert.DoesNotContain(fixture.Workspace.Definition.Components, component => component.SchemaNameString == TopicSchema);

            var response = await fixture.GetChangesAsync();

            Assert.Equal(200, response.Code);
            var change = Assert.Single(response.LocalChanges.Where(change => change.SchemaName == TopicSchema));
            Assert.Equal(ChangeType.Update, change.ChangeType);
            Assert.Equal(TopicPath, change.Uri);
            Assert.Equal(text, fixture.TopicDocument.Text);
            Assert.Equal(saved ? text : CleanTopic, File.ReadAllText(fixture.TopicFile));
            Assert.Equal(fixture.OriginalCache, File.ReadAllText(fixture.CacheFile));
            Assert.Throws<WorkspaceValidationException>(() => SyncHandler.ThrowIfWorkspaceUnreadable(fixture.Workspace));
        }

        [Fact]
        public async Task DeletedTopic_RemainsDeletedAlongsideAConflictedTopic()
        {
            using var fixture = new WorkspaceFixture();
            fixture.SetTopic(ConflictedTopic);
            var deletedFile = Path.Combine(fixture.Root, "topics", "ThankYou.mcs.yml");
            File.Delete(deletedFile);
            fixture.Workspace.RemoveDocument(new FilePath(deletedFile.Replace('\\', '/')));

            var response = await fixture.GetChangesAsync();

            Assert.Equal(200, response.Code);
            Assert.Equal(ChangeType.Update, Assert.Single(response.LocalChanges.Where(change => change.SchemaName == TopicSchema)).ChangeType);
            var deletion = Assert.Single(response.LocalChanges.Where(change => change.SchemaName == "test_agent.topic.ThankYou"));
            Assert.Equal(ChangeType.Delete, deletion.ChangeType);
            Assert.Equal("topics/ThankYou.mcs.yml", deletion.Uri);
        }

        [Fact]
        public async Task DeletingTheConflictedFile_ChangesThePreviewToDeleted()
        {
            using var fixture = new WorkspaceFixture();
            fixture.SetTopic(ConflictedTopic);
            await fixture.GetChangesAsync();
            File.Delete(fixture.TopicFile);
            fixture.Workspace.RemoveDocument(fixture.TopicDocument.FilePath);

            var response = await fixture.GetChangesAsync();

            Assert.Equal(200, response.Code);
            Assert.Equal(ChangeType.Delete, Assert.Single(response.LocalChanges.Where(change => change.SchemaName == TopicSchema)).ChangeType);
        }

        [Theory]
        [InlineData(CleanTopic, false)]
        [InlineData("kind: AdaptiveDialog\nbeginDialog:\n  kind: OnRecognizedIntent\n  id: main\n  intent:\n    triggerQueries:\n      - edited goodbye\n", true)]
        public async Task ResolvingTheConflict_UsesTheCurrentDocument(string resolved, bool remainsModified)
        {
            using var fixture = new WorkspaceFixture();
            fixture.SetTopic(ConflictedTopic);
            await fixture.GetChangesAsync();
            fixture.SetTopic(resolved, saved: false);

            var response = await fixture.GetChangesAsync();
            var topicChanges = response.LocalChanges.Where(change => change.SchemaName == TopicSchema);

            Assert.Equal(200, response.Code);
            if (remainsModified)
            {
                Assert.Equal(ChangeType.Update, Assert.Single(topicChanges).ChangeType);
            }
            else
            {
                Assert.Empty(topicChanges);
            }
            Assert.Empty(fixture.Workspace.GetUnreadableDocuments());
            Assert.Equal(fixture.OriginalCache, File.ReadAllText(fixture.CacheFile));
        }

        [Theory]
        [InlineData("topics/Goodbye.mcs.yml", ChangeType.Update)]
        [InlineData("topics\\Goodbye.mcs.yml", ChangeType.Update)]
        [InlineData("topics/goodbye.mcs.yml", ChangeType.Delete)]
        public void DisplayProjection_PreservesIdentityWithoutMutatingTheSyncChange(string path, ChangeType expected)
        {
            var workspace = new Mock<IMcsWorkspace>();
            workspace.Setup(value => value.GetUnreadableDocuments()).Returns(
                new[] { (new AgentFilePath(TopicPath), (Exception)new InvalidOperationException("Unresolved merge conflict.")) });
            var original = new Change
            {
                Uri = path,
                Name = "Goodbye",
                SchemaName = TopicSchema,
                ChangeKind = nameof(DialogComponent),
                ChangeType = ChangeType.Delete,
            };

            var displayed = Assert.Single(LocalChangeDisplay.ForWorkspace(workspace.Object, ImmutableArray.Create(original)));

            Assert.Equal(expected, displayed.ChangeType);
            Assert.Equal(original with { ChangeType = expected }, displayed);
            Assert.Equal(ChangeType.Delete, original.ChangeType);
        }

        [Theory]
        [InlineData(ChangeType.Create)]
        [InlineData(ChangeType.Update)]
        public void DisplayProjection_WithoutDeletes_DoesNotRevalidateTheWorkspace(ChangeType changeType)
        {
            var workspace = new Mock<IMcsWorkspace>(MockBehavior.Strict);
            var changes = ImmutableArray.Create(new Change { Uri = TopicPath, ChangeType = changeType });

            Assert.Equal(changes, LocalChangeDisplay.ForWorkspace(workspace.Object, changes));
            workspace.VerifyNoOtherCalls();
        }

        private sealed class WorkspaceFixture : IDisposable
        {
            private readonly World _world;
            private readonly GetLocalChangeHandler _handler;

            public WorkspaceFixture()
            {
                Root = Path.Combine(Path.GetTempPath(), "conflict-preview-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path.Combine(Root, "topics"));
                var fileFactory = new FileAccessorFactory();
                var accessor = fileFactory.Create(new DirectoryPath(Root.Replace('\\', '/')));
                var entity = CodeSerializer.Deserialize<BotEntity>("schemaName: test_agent\ntemplate: default-1.0.0\n")!;
                var components = new[] { "Goodbye", "ThankYou" }.Select(name => new DialogComponent(
                    schemaName: "test_agent.topic." + name,
                    displayName: name,
                    description: string.Empty,
                    id: Guid.NewGuid(),
                    parentBotComponentId: default,
                    dialog: CodeSerializer.Deserialize<AdaptiveDialog>(CleanTopic)!));
                WorkspaceSynchronizer.WriteCloudCache(accessor, new BotDefinition().WithEntity(entity).WithComponents(components));
                File.WriteAllText(Path.Combine(Root, "settings.mcs.yml"), CodeSerializer.Serialize(entity));
                File.WriteAllText(TopicFile, CleanTopic);
                File.WriteAllText(Path.Combine(Root, "topics", "ThankYou.mcs.yml"), CleanTopic);
                OriginalCache = File.ReadAllText(CacheFile);
                _world = new World(Root);
                Workspace = _world.GetWorkspace();
                Workspace.BuildCompilationModel();
                TopicDocument = _world.GetDocument(TopicFile)!;
                Assert.NotNull(TopicDocument);
                var synchronizer = new WorkspaceSynchronizer(
                    new SyncMcsFileParser(LspProjectorService.Instance),
                    fileFactory,
                    Mock.Of<IIslandControlPlaneService>(),
                    Mock.Of<ISyncProgress>(),
                    new LspComponentPathResolver());
                _handler = new GetLocalChangeHandler(synchronizer, Mock.Of<ILspLogger>());
            }

            public string Root { get; }
            public string TopicFile => Path.Combine(Root, "topics", "Goodbye.mcs.yml");
            public string CacheFile => Path.Combine(Root, ".mcs", "botdefinition.json");
            public string OriginalCache { get; }
            public McsWorkspace Workspace { get; }
            public McsLspDocument TopicDocument { get; }

            public void SetTopic(string text, bool saved = true)
            {
                if (saved)
                {
                    File.WriteAllText(TopicFile, text);
                }
                TopicDocument.UpdateText(text);
                Workspace.BuildCompilationModel();
            }

            public Task<SyncAgentResponse> GetChangesAsync()
                => _handler.HandleRequestAsync(
                    new DiffLocalRequest { WorkspaceUri = new Uri(Root) },
                    _world.GetRequestContext(_world.GetDocument(Path.Combine(Root, "settings.mcs.yml"))!, 0),
                    CancellationToken.None);

            public void Dispose() => Directory.Delete(Root, recursive: true);
        }
    }
}
