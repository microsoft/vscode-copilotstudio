namespace Microsoft.PowerPlatformLS.UnitTests.Impl.Language.CopilotStudio
{
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.DependencyInjection;
    using Microsoft.PowerPlatformLS.UnitTests.TestUtilities;
    using System;
    using System.IO;
    using System.Linq;
    using System.Threading.Tasks;
    using Xunit;

    public class DidSaveMethodTests
    {
        /// <summary>
        /// Test our server resiliency to config change such as requiring text on save event.
        /// Typically we don't require it and rely on incremental updates.
        /// </summary>
        [Fact]
        public async Task Success_OnSaveWithText_Async()
        {
            await using var context = new TestHost([new McsLspModule(), new TestFileModule()]);
            await context.InitializeLanguageServerAsync("file:///");
            var uri = new Uri("file:///c:/settings.mcs.yml");
            await context.OpenFileAsync(documentUri: uri);
            context.SaveDocument(uri, text: "prop: value");

            await GetNextDiagnosticAsync(context, "agent.mcs.yml");
            var diagParams = await GetNextDiagnosticAsync(context, "settings.mcs.yml");
            Assert.Equal(2, diagParams.Diagnostics.Length);
            Assert.All(diagParams.Diagnostics.Select(x => x.Severity), sev => Assert.Equal(DiagnosticSeverity.Error, sev));
            Assert.All(diagParams.Diagnostics.Select(x => x.Message), msg => Assert.StartsWith("Missing required property", msg));
            TestAssert.StringArrayEqual(["Missing required property 'SchemaName'", "Missing required property 'CdsBotId'"], diagParams.Diagnostics.Select(x => x.Message).ToArray());

            // no workspace - only one diagnostics message should be emitted
            Assert.False(context.TestStream.TryReadMessage(out _));
        }

        /// <summary>
        /// Make sure that 'save' returns ALL diagnostics fot the workspace, including errors that are not emitted by OM validation. (e.g. file location errors)
        /// </summary>
        [Fact]
        public async Task Success_OnSaveWithDocumentDiagnostic_Async()
        {
            await using var context = new TestHost([new McsLspModule(), new TestFileModule(false)]);
            await context.InitializeLanguageServerAsync("file:///");
            var uri = new Uri("file:///c:/agent2.mcs.yml");
            var openDiagnostic = await context.OpenFileAsync(filename: "DidChange/agent.mcs.yml", documentUri: uri);
            Assert.Single(openDiagnostic.Diagnostics);
            context.SaveDocument(uri, text: "kind: GptComponentMetadata\ninstructions: my kingdom for a test");

            var saveDiagnostic = await GetNextDiagnosticAsync(context, "agent2.mcs.yml");
            Assert.Single(saveDiagnostic.Diagnostics);
            var saveDiagnData = saveDiagnostic.Diagnostics.First();
            Assert.Equal(DiagnosticSeverity.Warning, saveDiagnData.Severity);
            Assert.Equal("Elements of type 'GptComponentMetadata' are expected in 'agent.mcs.yml'.", saveDiagnData.Message);
            Assert.Equal(openDiagnostic.Diagnostics.First().Message, saveDiagnData.Message);
        }

        private static async Task<DiagnosticsParams> GetNextDiagnosticAsync(TestHost context, string expectedFileName)
        {
            var saveDiagBaseMsg = await context.GetResponseAsync([LspMethods.Diagnostics]);
            var saveDiagMsg = saveDiagBaseMsg as LspJsonRpcMessage;
            Assert.Equal(LspMethods.Diagnostics, saveDiagMsg?.Method);
            var diagParams = JsonRpc.GetValidParams<DiagnosticsParams>(saveDiagMsg);
            Assert.EndsWith(expectedFileName, diagParams.Uri.AbsolutePath);
            return diagParams;
        }

        [Fact]
        public async Task Success_OnDidSave_Async()
        {
            await using var context = new TestHost();
            var workspacePath = Path.GetFullPath("TestData/Workspace/LocalWorkspace");
            await context.InitializeLanguageServerAsync(workspacePath);

            const string UseGlobalVariable = @"
    - kind: SetVariable
      id: userVariable_zero
      variable: Topic.One
      value: = Global.Zero + 1
";
            const string DeclareVariable = @"
    - kind: SetVariable
      id: setVariable_zero
      variable: Global.Zero
      value: 0
";

            var goodbyeUri = new Uri(Path.Combine(workspacePath, "topics/Goodbye.mcs.yml"));
            var greetingUri = new Uri(Path.Combine(workspacePath, "topics/Greeting.mcs.yml"));

            // introduce an error in Goodbye
            {
                // LSP specs specify that file should be "opened" before "changing" it but we don't enforce it.
                // This may change and require: context.OpenFileAsync(filename: "Workspace/LocalWorkspace/topics/Goodbye.mcs.yml", documentUri: goodbyeUri);
                var change = ChangeEvents.InsertTextAt(UseGlobalVariable, 22, 0);
                var changeBaseResponse = await context.ChangeFileAsync(change, goodbyeUri);
                var changeResponse = changeBaseResponse as LspJsonRpcMessage;
                var changeResult = JsonRpc.GetValidParams<DiagnosticsParams>(changeResponse);
                var diag = changeResult.Diagnostics.Single();
                Assert.Equal(DiagnosticSeverity.Error, diag.Severity);
                Assert.Equal("Identifier not recognized in expression:  Global.Zero + 1", diag.Message);
            }

            // fix previous error in Goodbye by declaring a global variable in Greeting and saving
            {
                var change = ChangeEvents.InsertTextAt(DeclareVariable, 22, 0);
                var changeResponse = await context.ChangeFileAsync(change, greetingUri) as LspJsonRpcMessage;
                var changeResult = JsonRpc.GetValidParams<DiagnosticsParams>(changeResponse);
                // assert we introduced no error
                Assert.Empty(changeResult.Diagnostics);
                // save for clearing previous diagnostics
                context.SaveDocument(greetingUri);

                var goodbyeDiagnostics = await ReadAllMessagesUntilDiagnosticsAsync(context, "Goodbye.mcs.yml");
                Assert.Empty(goodbyeDiagnostics?.Diagnostics);
            }

            // renaming the global variable and saving may also introduce new errors
            {
                // rename variable "Global.Zero" to "Global.Zero0"
                var change = ChangeEvents.InsertTextAt("0", 25, 27);
                var changeResponse = await context.ChangeFileAsync(change, greetingUri) as LspJsonRpcMessage;
                var changeResult = JsonRpc.GetValidParams<DiagnosticsParams>(changeResponse);
                var greetingDiagnostics = changeResult.Uri.AbsolutePath.EndsWith("Greeting.mcs.yml") ? changeResult : await ReadAllMessagesUntilDiagnosticsAsync(context, "Greeting.mcs.yml");
                // assert we introduced no error directly
                Assert.Empty(changeResult.Diagnostics);
                // save to propagate variable name change and introduce new diagnostics
                context.SaveDocument(greetingUri);
                var goodbyeDiagnostics = await ReadAllMessagesUntilDiagnosticsAsync(context, "Goodbye.mcs.yml");
                var diag = goodbyeDiagnostics!.Diagnostics.Single();
                Assert.Equal(DiagnosticSeverity.Error, diag.Severity);
                Assert.Contains("Identifier not recognized in expression:  Global.Zero + 1", diag.Message);
            }
        }

        private async Task<DiagnosticsParams?> ReadAllMessagesUntilDiagnosticsAsync(TestHost context, string documentName, int maxAttempts = 30)
        {
            for (int idx = 0; idx < maxAttempts; ++idx)
            {
                var saveDiagMsg = await context.GetResponseAsync([LspMethods.Diagnostics]) as LspJsonRpcMessage;
                Assert.Equal(LspMethods.Diagnostics, saveDiagMsg?.Method);
                var diagParams = JsonRpc.GetValidParams<DiagnosticsParams>(saveDiagMsg);
                if (diagParams.Uri.AbsolutePath.EndsWith(documentName))
                {
                    return diagParams;
                }
            }

            return null;
        }
    }
}