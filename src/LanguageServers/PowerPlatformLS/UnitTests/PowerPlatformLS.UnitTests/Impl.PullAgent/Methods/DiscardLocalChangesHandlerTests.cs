namespace Microsoft.PowerPlatformLS.UnitTests.Impl.PullAgent.Methods
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.Agents.Platform.Content;
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.CopilotStudio.Sync;
    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Impl.PullAgent;
    using Microsoft.PowerPlatformLS.UnitTests.Impl.Language.CopilotStudio;
    using Moq;
    using System.Collections.Immutable;
    using Xunit;

    public class DiscardLocalChangesHandlerTests
    {
        [Fact]
        public async Task DiscardLocalChanges_UsesCompiledWorkspaceChanges()
        {
            var workspacePath = Path.GetFullPath("TestData/Workspace/LocalWorkspace");
            var world = new World(workspacePath);
            var document = world.GetDocument(Path.Combine(workspacePath, "topics/Goodbye.mcs.yml"));
            Assert.NotNull(document);
            var requestContext = world.GetRequestContext(document!, 0);
            var workspace = (Microsoft.PowerPlatformLS.Contracts.FileLayout.IMcsWorkspace)requestContext.Workspace;
            var projectedDefinition = new BotDefinition().WithComponentCollections([
                CodeSerializer.Deserialize<BotComponentCollection>(
                    "schemaName: bot_componentcollection_deleted\ndisplayName: Deleted Collection\n")!
            ]);
            var referenceChange = new Change
            {
                ChangeType = ChangeType.Delete,
                ChangeKind = nameof(BotComponentCollection),
                SchemaName = "bot_componentcollection_deleted",
                Uri = "references.mcs.yml",
            };
            var emptyChangeSet = new PvaComponentChangeSet(null, null, null);
            var synchronizer = new Mock<IWorkspaceSynchronizer>();
            synchronizer
                .Setup(service => service.ReadWorkspaceDefinitionForDiscardAsync(
                    workspace.FolderPath,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(((DefinitionBase)projectedDefinition, ImmutableArray<Change>.Empty));
            synchronizer
                .Setup(service => service.GetLocalChangesAsync(
                    workspace.FolderPath,
                    It.Is<DefinitionBase>(definition =>
                        IsCompiledWithoutCollections(definition, projectedDefinition)),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((emptyChangeSet, ImmutableArray.Create(referenceChange)));
            synchronizer
                .Setup(service => service.GetLocalChangesAsync(
                    workspace.FolderPath,
                    projectedDefinition,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((emptyChangeSet, ImmutableArray<Change>.Empty));
            synchronizer
                .Setup(service => service.DiscardLocalChanges(
                    workspace.FolderPath,
                    projectedDefinition,
                    It.IsAny<IReadOnlyCollection<Change>>()))
                .Returns<DirectoryPath, DefinitionBase, IReadOnlyCollection<Change>>(
                    (_, _, changes) => new DiscardResult { Deleted = changes.Count });

            var handler = new DiscardLocalChangesHandler(
                synchronizer.Object,
                new InMemoryFileAccessorFactory(),
                new Mock<ILspLogger>().Object);
            var response = await handler.HandleRequestAsync(
                new DiscardLocalChangesRequest { WorkspaceUri = new Uri(workspacePath) },
                requestContext,
                CancellationToken.None);

            Assert.Equal(200, response.Code);
            Assert.Equal(1, response.Result.Deleted);
        }

        [Fact]
        public async Task DiscardLocalChanges_UnreadableFile_IsStillDiscarded()
        {
            var workspacePath = Path.GetFullPath("TestData/Workspace/LocalWorkspace");
            var world = new World(workspacePath);
            var document = world.GetDocument(Path.Combine(workspacePath, "topics/Goodbye.mcs.yml"));
            Assert.NotNull(document);
            var requestContext = world.GetRequestContext(document!, 0);
            var workspace = (Microsoft.PowerPlatformLS.Contracts.FileLayout.IMcsWorkspace)requestContext.Workspace;
            var definition = new BotDefinition();
            var unreadableChange = new Change
            {
                ChangeType = ChangeType.Update,
                ChangeKind = BotElementKind.AdaptiveDialog.ToString(),
                SchemaName = "bot.topic.Broken",
                Uri = "topics/Broken.mcs.yml",
            };
            var emptyChangeSet = new PvaComponentChangeSet(null, null, null);
            var discarded = ImmutableArray<Change>.Empty;

            var synchronizer = new Mock<IWorkspaceSynchronizer>();
            synchronizer
                .Setup(service => service.ReadWorkspaceDefinitionForDiscardAsync(
                    workspace.FolderPath,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(((DefinitionBase)definition, ImmutableArray.Create(unreadableChange)));
            synchronizer
                .Setup(service => service.GetLocalChangesAsync(
                    workspace.FolderPath,
                    It.IsAny<DefinitionBase>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((emptyChangeSet, ImmutableArray<Change>.Empty));
            synchronizer
                .Setup(service => service.DiscardLocalChanges(
                    workspace.FolderPath,
                    It.IsAny<DefinitionBase>(),
                    It.IsAny<IReadOnlyCollection<Change>>()))
                .Returns<DirectoryPath, DefinitionBase, IReadOnlyCollection<Change>>((_, _, changes) =>
                {
                    discarded = changes.ToImmutableArray();
                    return new DiscardResult { Restored = changes.Count };
                });

            var handler = new DiscardLocalChangesHandler(
                synchronizer.Object,
                new InMemoryFileAccessorFactory(),
                new Mock<ILspLogger>().Object);
            var response = await handler.HandleRequestAsync(
                new DiscardLocalChangesRequest { WorkspaceUri = new Uri(workspacePath) },
                requestContext,
                CancellationToken.None);

            Assert.Equal(200, response.Code);
            Assert.Equal("topics/Broken.mcs.yml", Assert.Single(discarded).Uri);
            Assert.Equal(1, response.Result.Restored);
        }

        [Fact]
        public async Task DiscardLocalChanges_StandaloneWorkflowWorkspace_UsesWorkflowDiscard()
        {
            var workspacePath = Path.GetFullPath("TestData/Workspace/LocalWorkspace");
            var world = new World(workspacePath);
            var document = world.GetDocument(Path.Combine(workspacePath, "topics/Goodbye.mcs.yml"));
            Assert.NotNull(document);
            var requestContext = world.GetRequestContext(document!, 0);
            var workspace = (Microsoft.PowerPlatformLS.Contracts.FileLayout.IMcsWorkspace)requestContext.Workspace;
            var workflowChange = new Change
            {
                ChangeType = ChangeType.Update,
                ChangeKind = "Workflow",
                SchemaName = "Mcs.Workflow.11111111-1111-1111-1111-111111111111",
                Uri = "workflows/Daily_11111111-1111-1111-1111-111111111111/workflow.json",
            };
            var remaining = false;

            var synchronizer = new Mock<IWorkspaceSynchronizer>();
            synchronizer
                .Setup(service => service.GetStandaloneWorkflowChangesForDiscardAsync(
                    workspace.FolderPath,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => (remaining ? ImmutableArray<Change>.Empty : ImmutableArray.Create(workflowChange), ImmutableArray<Change>.Empty));
            synchronizer
                .Setup(service => service.DiscardStandaloneWorkflowChanges(
                    workspace.FolderPath,
                    It.IsAny<IReadOnlyCollection<Change>>()))
                .Returns<DirectoryPath, IReadOnlyCollection<Change>>((_, changes) =>
                {
                    remaining = true;
                    return new DiscardResult { Restored = changes.Count };
                });

            var handler = new DiscardLocalChangesHandler(
                synchronizer.Object,
                CreateStandaloneWorkflowFactory(workspace.FolderPath),
                new Mock<ILspLogger>().Object);
            var response = await handler.HandleRequestAsync(
                new DiscardLocalChangesRequest { WorkspaceUri = new Uri(workspacePath) },
                requestContext,
                CancellationToken.None);

            Assert.Equal(200, response.Code);
            Assert.Equal(1, response.Result.Restored);
            Assert.Empty(response.LocalChanges);
            synchronizer.Verify(
                service => service.ReadWorkspaceDefinitionForDiscardAsync(It.IsAny<DirectoryPath>(), It.IsAny<CancellationToken>()),
                Times.Never);
            synchronizer.Verify(
                service => service.GetLocalChangesAsync(It.IsAny<DirectoryPath>(), It.IsAny<DefinitionBase>(), It.IsAny<CancellationToken>()),
                Times.Never);
            synchronizer.Verify(
                service => service.DiscardLocalChanges(It.IsAny<DirectoryPath>(), It.IsAny<DefinitionBase>(), It.IsAny<IReadOnlyCollection<Change>>()),
                Times.Never);
        }

        [Fact]
        public async Task DiscardLocalChanges_StandaloneWorkflowWorkspace_RestoresUnparsableFiles()
        {
            var workspacePath = Path.GetFullPath(Path.Combine("TestData", "Workspace", "LocalWorkspace"));
            var world = new World(workspacePath);
            var document = world.GetDocument(Path.Combine(workspacePath, "topics/Goodbye.mcs.yml"));
            Assert.NotNull(document);
            var requestContext = world.GetRequestContext(document!, 0);
            var workspace = (Microsoft.PowerPlatformLS.Contracts.FileLayout.IMcsWorkspace)requestContext.Workspace;
            var unreadable = new Change
            {
                ChangeType = ChangeType.Update,
                ChangeKind = "Workflow",
                SchemaName = "Mcs.Workflow.11111111-1111-1111-1111-111111111111.metadata",
                Uri = "workflows/Daily_11111111-1111-1111-1111-111111111111/metadata.yml",
            };
            var remaining = false;
            IReadOnlyCollection<Change>? discarded = null;

            var synchronizer = new Mock<IWorkspaceSynchronizer>();
            synchronizer
                .Setup(service => service.GetStandaloneWorkflowChangesForDiscardAsync(
                    workspace.FolderPath,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => (ImmutableArray<Change>.Empty, remaining ? ImmutableArray<Change>.Empty : ImmutableArray.Create(unreadable)));
            synchronizer
                .Setup(service => service.DiscardStandaloneWorkflowChanges(
                    workspace.FolderPath,
                    It.IsAny<IReadOnlyCollection<Change>>()))
                .Returns<DirectoryPath, IReadOnlyCollection<Change>>((_, changes) =>
                {
                    remaining = true;
                    discarded = changes;
                    return new DiscardResult { Restored = changes.Count };
                });

            var handler = new DiscardLocalChangesHandler(
                synchronizer.Object,
                CreateStandaloneWorkflowFactory(workspace.FolderPath),
                new Mock<ILspLogger>().Object);
            var response = await handler.HandleRequestAsync(
                new DiscardLocalChangesRequest { WorkspaceUri = new Uri(workspacePath) },
                requestContext,
                CancellationToken.None);

            Assert.Equal(200, response.Code);
            Assert.Equal(1, response.Result.Restored);
            Assert.Equal(unreadable.SchemaName, Assert.Single(discarded!).SchemaName);
            Assert.Empty(response.LocalChanges);
        }

        private static InMemoryFileAccessorFactory CreateStandaloneWorkflowFactory(DirectoryPath workspaceFolder)
        {
            var factory = new InMemoryFileAccessorFactory();
            using var stream = factory.Create(workspaceFolder).OpenWrite(new AgentFilePath(WorkflowWorkspace.ProjectFileName));
            using var writer = new StreamWriter(stream);
            writer.Write("kind: workflow\n");
            return factory;
        }

        private static bool IsCompiledWithoutCollections(
            DefinitionBase definition,
            DefinitionBase projectedDefinition)
        {
            return !ReferenceEquals(definition, projectedDefinition)
                && definition is BotDefinition bot
                && bot.ComponentCollections.IsEmpty;
        }
    }
}
