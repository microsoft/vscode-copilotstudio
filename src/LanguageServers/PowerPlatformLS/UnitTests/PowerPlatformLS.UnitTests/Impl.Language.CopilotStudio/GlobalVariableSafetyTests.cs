namespace Microsoft.PowerPlatformLS.UnitTests.Impl.Language.CopilotStudio
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Handlers;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Models;
    using System.IO;
    using System.Linq;
    using System.Threading.Tasks;
    using Xunit;

    public class GlobalVariableSafetyTests
    {
        private static readonly string WorkspacePath = Path.GetFullPath(Path.Combine("TestData", "Workspace", "GlobalVarWorkspace"));

        [Fact]
        public async Task Rename_EditsTargetSourceFilesOnly_NeverCloudCacheAsync()
        {
            var world = new World(WorkspacePath);
            var document = world.GetDocument(Path.Combine(WorkspacePath, "topics", "ThankYou.mcs.yml"))!;
            var requestContext = world.GetRequestContext(document, "variable: Global.Va|r1");
            var handler = world.GetHandler<RenameHandler>();

            var position = requestContext.GetTextDocumentPositionParams();
            var edit = await handler.HandleRequestAsync(new RenameParams { TextDocument = position.TextDocument, Position = position.Position, NewName = "MyNewVar" }, requestContext, default);

            Assert.NotNull(edit);
            foreach (var uri in edit!.DocumentChanges!.SelectMany(TargetUris))
            {
                Assert.DoesNotContain("/.mcs/", uri.Replace('\\', '/'));
                Assert.DoesNotContain("botdefinition.json", uri);
                Assert.DoesNotContain("changetoken.txt", uri);
            }
        }

        [Fact]
        public void CreateVariable_ScaffoldRoundTripsToResolvableGlobal()
        {
            var world = new World(WorkspacePath);
            world.GetDocument(Path.Combine(WorkspacePath, "variables", "Var1.mcs.yml"));
            var workspace = world.GetWorkspace();

            var scaffold = "mcs.metadata:\n  componentName: Var3\nkind: Variable\nname: Var3\nscope: Conversation\nisExternalInitializationAllowed: false\nisOutputToExternalCallers: false\ninitializationTimeoutInMilliseconds: 0\n";
            var normalizedRoot = WorkspacePath.Replace('\\', '/');
            var newDocument = new McsLspDocument(new FilePath($"{normalizedRoot}/variables/Var3.mcs.yml"), scaffold, new DirectoryPath(normalizedRoot));
            workspace.AddDocument(newDocument);
            workspace.BuildCompilationModel();

            var created = workspace.Definition.DescendantsAndSelf().OfType<GlobalVariableComponent>().FirstOrDefault(component => component.SchemaNameString != null && component.SchemaNameString.EndsWith(".globalvariable.Var3"));
            Assert.NotNull(created);
        }

        private static System.Collections.Generic.IEnumerable<string> TargetUris(IFileOperation operation) => operation switch
        {
            TextDocumentEdit edit => [edit.TextDocument.Uri.ToString()],
            RenameFile rename => [rename.OldUri.ToString(), rename.NewUri.ToString()],
            CreateFile create => [create.Uri.ToString()],
            _ => [],
        };
    }
}
