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
                .Setup(service => service.ReadWorkspaceDefinitionAsync(
                    workspace.FolderPath,
                    It.IsAny<CancellationToken>(),
                    true))
                .ReturnsAsync(projectedDefinition);
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
                new Mock<ILspLogger>().Object);
            var response = await handler.HandleRequestAsync(
                new DiscardLocalChangesRequest { WorkspaceUri = new Uri(workspacePath) },
                requestContext,
                CancellationToken.None);

            Assert.Equal(200, response.Code);
            Assert.Equal(1, response.Result.Deleted);
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
