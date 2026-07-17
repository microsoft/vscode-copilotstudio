namespace Microsoft.PowerPlatformLS.UnitTests.Impl.Language.CopilotStudio
{
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Handlers;
    using System.IO;
    using System.Linq;
    using System.Threading.Tasks;
    using Xunit;

    public class GlobalVariableRenameHandlerTests
    {
        private static readonly string WorkspacePath = Path.GetFullPath(Path.Combine("TestData", "Workspace", "GlobalVarWorkspace"));

        [Fact]
        public async Task PrepareRename_OnSetVariableTarget_ReturnsRangeAndPlaceholderAsync()
        {
            var world = new World(WorkspacePath);
            var document = world.GetDocument(Path.Combine(WorkspacePath, "topics", "ThankYou.mcs.yml"))!;
            var requestContext = world.GetRequestContext(document, "variable: Global.Va|r1");
            var handler = world.GetHandler<PrepareRenameHandler>();

            Assert.False(handler.MutatesSolutionState);

            var result = await handler.HandleRequestAsync(requestContext.GetTextDocumentPositionParams(), requestContext, default);

            Assert.NotNull(result);
            Assert.Equal("Var1", result!.Placeholder);
            Assert.Equal("Var1", TextAt(world, document.Uri, result.Range));
        }

        [Fact]
        public async Task PrepareRename_OnDefinition_ReturnsRangeAndPlaceholderAsync()
        {
            var world = new World(WorkspacePath);
            var document = world.GetDocument(Path.Combine(WorkspacePath, "variables", "Var1.mcs.yml"))!;
            var requestContext = world.GetRequestContext(document, "name: Va|r1");
            var handler = world.GetHandler<PrepareRenameHandler>();

            var result = await handler.HandleRequestAsync(requestContext.GetTextDocumentPositionParams(), requestContext, default);

            Assert.NotNull(result);
            Assert.Equal("Var1", result!.Placeholder);
        }

        [Fact]
        public async Task PrepareRename_NotRenameable_ReturnsNullAsync()
        {
            var world = new World(WorkspacePath);
            var document = world.GetDocument(Path.Combine(WorkspacePath, "topics", "ThankYou.mcs.yml"))!;
            var requestContext = world.GetRequestContext(document, "activity: You'|re welcome.");
            var handler = world.GetHandler<PrepareRenameHandler>();

            var result = await handler.HandleRequestAsync(requestContext.GetTextDocumentPositionParams(), requestContext, default);

            Assert.Null(result);
        }

        [Fact]
        public async Task Rename_RewritesAllSitesAndRenamesFileAsync()
        {
            var world = new World(WorkspacePath);
            var document = world.GetDocument(Path.Combine(WorkspacePath, "topics", "ThankYou.mcs.yml"))!;
            var requestContext = world.GetRequestContext(document, "variable: Glob|al.Var1");
            var handler = world.GetHandler<RenameHandler>();

            Assert.False(handler.MutatesSolutionState);

            var edit = await handler.HandleRequestAsync(RenameRequest(requestContext, "MyNewVar"), requestContext, default);

            Assert.NotNull(edit);
            var operations = edit!.DocumentChanges!;

            var renameFile = operations.OfType<RenameFile>().Single();
            Assert.EndsWith("variables/Var1.mcs.yml", renameFile.OldUri.ToString());
            Assert.EndsWith("variables/MyNewVar.mcs.yml", renameFile.NewUri.ToString());

            var definitionEdit = operations.OfType<TextDocumentEdit>().Single(edit => edit.TextDocument.Uri.ToString().EndsWith("variables/Var1.mcs.yml"));
            Assert.Equal(2, definitionEdit.Edits.Length);
            Assert.All(definitionEdit.Edits, textEdit => Assert.Equal("MyNewVar", textEdit.NewText));

            Assert.Contains(operations.OfType<TextDocumentEdit>(), edit => edit.TextDocument.Uri.ToString().EndsWith("topics/ThankYou.mcs.yml"));
            Assert.Contains(operations.OfType<TextDocumentEdit>(), edit => edit.TextDocument.Uri.ToString().EndsWith("topics/NoNameThankYou.mcs.yml"));
            Assert.Contains(operations.OfType<TextDocumentEdit>(), edit => edit.TextDocument.Uri.ToString().EndsWith("topics/UseGlobalVar.mcs.yml"));

            Assert.All(operations.OfType<TextDocumentEdit>().SelectMany(edit => edit.Edits), textEdit => Assert.Equal("MyNewVar", textEdit.NewText));
        }

        [Theory]
        [InlineData("file:///c:/agent/variables/Var1.mcs.yml", "MyNewVar", "variables/MyNewVar.mcs.yml")]
        [InlineData("file:///c:/agent/variables/crf9a_agent.GlobalVariableComponent.OldVar.mcs.yml", "NewVar", "variables/crf9a_agent.GlobalVariableComponent.NewVar.mcs.yml")]
        public void ChangeFileName_SwapsOnlyTheVariableNameSegment(string sourceUri, string newVariableName, string expectedSuffix)
        {
            var result = RenameEditFactory.ChangeFileName(new System.Uri(sourceUri), newVariableName);

            Assert.EndsWith(expectedSuffix, result.ToString());
        }

        [Fact]
        public async Task Rename_PrefixedDefinitionFile_RenamesFileAndComponentNameAsync()
        {
            var world = new World(WorkspacePath);
            var document = world.GetDocument(Path.Combine(WorkspacePath, "topics", "UsePrefixVar.mcs.yml"))!;
            var requestContext = world.GetRequestContext(document, "variable: Global.Pref|ixVar");
            var handler = world.GetHandler<RenameHandler>();

            var edit = await handler.HandleRequestAsync(RenameRequest(requestContext, "RenamedPrefixVar"), requestContext, default);

            Assert.NotNull(edit);
            var operations = edit!.DocumentChanges!;

            var renameFile = operations.OfType<RenameFile>().Single();
            Assert.EndsWith("variables/cree9_agent.GlobalVariableComponent.PrefixVar.mcs.yml", renameFile.OldUri.ToString());
            Assert.EndsWith("variables/cree9_agent.GlobalVariableComponent.RenamedPrefixVar.mcs.yml", renameFile.NewUri.ToString());

            var definitionEdit = operations.OfType<TextDocumentEdit>().Single(textDocumentEdit => textDocumentEdit.TextDocument.Uri.ToString().EndsWith("cree9_agent.GlobalVariableComponent.PrefixVar.mcs.yml"));
            Assert.Equal(2, definitionEdit.Edits.Length);
            Assert.All(definitionEdit.Edits, textEdit => Assert.Equal("RenamedPrefixVar", textEdit.NewText));

            Assert.Contains(operations.OfType<TextDocumentEdit>(), textDocumentEdit => textDocumentEdit.TextDocument.Uri.ToString().EndsWith("topics/UsePrefixVar.mcs.yml"));
            Assert.All(operations.OfType<TextDocumentEdit>().SelectMany(textDocumentEdit => textDocumentEdit.Edits), textEdit => Assert.Equal("RenamedPrefixVar", textEdit.NewText));
        }

        [Fact]
        public async Task Rename_InvalidName_ReturnsNullAsync()
        {
            var world = new World(WorkspacePath);
            var document = world.GetDocument(Path.Combine(WorkspacePath, "topics", "ThankYou.mcs.yml"))!;
            var requestContext = world.GetRequestContext(document, "variable: Glob|al.Var1");
            var handler = world.GetHandler<RenameHandler>();

            var edit = await handler.HandleRequestAsync(RenameRequest(requestContext, "bad name!"), requestContext, default);

            Assert.Null(edit);
        }

        [Fact]
        public async Task Rename_CollisionWithExistingGlobal_ThrowsExplanatoryErrorAsync()
        {
            var world = new World(WorkspacePath);
            var document = world.GetDocument(Path.Combine(WorkspacePath, "topics", "ThankYou.mcs.yml"))!;
            var requestContext = world.GetRequestContext(document, "variable: Glob|al.Var1");
            var handler = world.GetHandler<RenameHandler>();

            var exception = await Assert.ThrowsAsync<System.InvalidOperationException>(() => handler.HandleRequestAsync(RenameRequest(requestContext, "Var2"), requestContext, default));

            Assert.Contains("Var2", exception.Message);
            Assert.Contains("already exists", exception.Message);
        }

        [Fact]
        public async Task Rename_NotOnGlobalVariable_ReturnsNullAsync()
        {
            var world = new World(WorkspacePath);
            var document = world.GetDocument(Path.Combine(WorkspacePath, "topics", "ThankYou.mcs.yml"))!;
            var requestContext = world.GetRequestContext(document, "activity: You'|re welcome.");
            var handler = world.GetHandler<RenameHandler>();

            var edit = await handler.HandleRequestAsync(RenameRequest(requestContext, "MyNewVar"), requestContext, default);

            Assert.Null(edit);
        }

        [Fact]
        public async Task Rename_CaseOnlyCollisionWithExistingGlobal_ThrowsExplanatoryErrorAsync()
        {
            var world = new World(WorkspacePath);
            var document = world.GetDocument(Path.Combine(WorkspacePath, "topics", "ThankYou.mcs.yml"))!;
            var requestContext = world.GetRequestContext(document, "variable: Global.Va|r1");
            var handler = world.GetHandler<RenameHandler>();

            var exception = await Assert.ThrowsAsync<System.InvalidOperationException>(() => handler.HandleRequestAsync(RenameRequest(requestContext, "VAR2"), requestContext, default));

            Assert.Contains("VAR2", exception.Message);
            Assert.Contains("already exists", exception.Message);
        }

        private static RenameParams RenameRequest(RequestContext requestContext, string newName)
        {
            var position = requestContext.GetTextDocumentPositionParams();
            return new RenameParams
            {
                TextDocument = position.TextDocument,
                Position = position.Position,
                NewName = newName,
            };
        }

        private static string TextAt(World world, System.Uri uri, Range range)
        {
            var document = world.GetDocument(uri)!;
            var start = document.MarkResolver.GetIndex(range.Start);
            var end = document.MarkResolver.GetIndex(range.End);
            return document.Text.Substring(start, end - start);
        }
    }
}
