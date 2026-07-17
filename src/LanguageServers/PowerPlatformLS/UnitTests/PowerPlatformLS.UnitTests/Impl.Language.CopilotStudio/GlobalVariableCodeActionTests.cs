namespace Microsoft.PowerPlatformLS.UnitTests.Impl.Language.CopilotStudio
{
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio;
    using System.IO;
    using System.Linq;
    using Xunit;

    public class GlobalVariableCodeActionTests
    {
        private static readonly string WorkspacePath = Path.GetFullPath(Path.Combine("TestData", "Workspace", "GlobalVarWorkspace"));

        [Fact]
        public void UndeclaredGlobal_OffersCreateAndChangeQuickFixes()
        {
            var quickFixes = GetQuickFixes("UnknownGlobalRef.mcs.yml");

            var createAction = quickFixes.Single(action => action.Title == "Create global variable 'Var3'");
            Assert.Equal("microsoft-copilot-studio.createGlobalVariable", createAction.Command!.Command);

            var args = Assert.IsType<CreateGlobalVariableCommandArgs>(createAction.Command!.Arguments!.Single());
            Assert.EndsWith("variables/Var3.mcs.yml", args.NewFileUri);
            Assert.Contains("kind: Variable", args.FileContent);
            Assert.Contains("name: Var3", args.FileContent);
            Assert.Contains("scope: Conversation", args.FileContent);
            Assert.Contains("componentName: Var3", args.FileContent);
            Assert.Contains("isOutputToExternalCallers: false", args.FileContent);

            Assert.Contains(quickFixes, action => action.Title == "Change to 'Var1'");
            Assert.Contains(quickFixes, action => action.Title == "Change to 'Var2'");
        }

        [Fact]
        public void ChangeToExisting_ReplacesIdentifierWithExistingGlobal()
        {
            var quickFixes = GetQuickFixes("UnknownGlobalRef.mcs.yml");

            var changeAction = quickFixes.First(action => action.Title == "Change to 'Var1'");
            var edit = changeAction.Edit!.DocumentChanges!.OfType<TextDocumentEdit>().Single().Edits.Single();
            Assert.Equal("Var1", edit.NewText);
        }

        [Fact]
        public void DeclaredGlobalUsage_HasNoCreateQuickFix()
        {
            var quickFixes = GetQuickFixes("ThankYou.mcs.yml");

            Assert.DoesNotContain(quickFixes, action => action.Title.StartsWith("Create global variable"));
        }

        [Fact]
        public void CreateGlobalVariable_AlsoInsertsSetVariableInitializerAtTopOfActions()
        {
            var quickFixes = GetQuickFixes("UnknownGlobalRef.mcs.yml");
            var createAction = quickFixes.Single(action => action.Title == "Create global variable 'Var3'");

            var args = Assert.IsType<CreateGlobalVariableCommandArgs>(createAction.Command!.Arguments!.Single());
            Assert.EndsWith("UnknownGlobalRef.mcs.yml", args.DocumentUri);

            var setVariable = args.SetVariable!;
            Assert.Equal(0, setVariable.Character);
            Assert.StartsWith("    - kind: SetVariable\n", setVariable.TextBeforeValue);
            Assert.Contains("      variable: Global.Var3\n", setVariable.TextBeforeValue);
            Assert.Contains("      id: setVariable_", setVariable.TextBeforeValue);
            Assert.EndsWith("      value: ", setVariable.TextBeforeValue);
            Assert.Equal("\n", setVariable.TextAfterValue);
        }

        [Fact]
        public void UnrelatedIdentifierError_DoesNotMisattributeGlobalVariableQuickFixes()
        {
            var diagnostics = GetDiagnostics("WrongDiagnosticRef.mcs.yml");
            Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "IdentifierNotRecognized");

            var quickFixes = GetQuickFixes("WrongDiagnosticRef.mcs.yml");
            Assert.DoesNotContain(quickFixes, action => action.Title.StartsWith("Create global variable"));
            Assert.DoesNotContain(quickFixes, action => action.Title.StartsWith("Change to '"));
        }

        private static IReadOnlyList<CodeAction> GetQuickFixes(string topicFileName)
        {
            return GetDiagnostics(topicFileName)
                .SelectMany(diagnostic => diagnostic.Data?.Quickfix ?? System.Array.Empty<CodeAction>())
                .ToList();
        }

        private static IReadOnlyList<Diagnostic> GetDiagnostics(string topicFileName)
        {
            var world = new World(WorkspacePath);
            var document = world.GetDocument(Path.Combine(WorkspacePath, "topics", topicFileName))!;
            var requestContext = world.GetRequestContext(document, 0);
            var workspace = world.GetWorkspace();

            return workspace.GetDiagnostics(requestContext)
                .Where(parameters => parameters.Uri.ToString().EndsWith(topicFileName))
                .SelectMany(parameters => parameters.Diagnostics)
                .ToList();
        }
    }
}
