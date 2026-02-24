namespace Microsoft.PowerPlatformLS.UnitTests.Impl.Core
{
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Impl.Core.Lsp.Uris;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.DependencyInjection;
    using Microsoft.PowerPlatformLS.Impl.Language.Yaml.DependencyInjection;
    using Microsoft.PowerPlatformLS.UnitTests.TestUtilities;
    using System; // Required for StringComparison
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading.Tasks;
    using Xunit;

    public class LanguageServerTests
    {
        [Fact]
        public async Task FailCompletion_OnBeforeInitialize_Async()
        {
            await using var context = new TestHost();

            context.SendCompletionRequest();

            var response = await context.GetResponseAsync();

            Assert.Equal(ErrorCodes.InternalError, (response as JsonRpcResponse)?.Error?.Code);
            Assert.StartsWith("System.InvalidOperationException: initialize request must complete first", context.Logs.Error.First());
        }

        [Fact]
        public async Task FailCompletion_OnBeforeDidOpen_Async()
        {
            await using var context = new TestHost();
            await context.InitializeLanguageServerAsync();
            context.SendCompletionRequest();
            var response = await context.GetResponseAsync() as JsonRpcResponse;
            Assert.NotNull(response);
        }

        [Theory]
        [InlineData(true, true)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(false, false)]
        public async Task SuccessDiagnostics_OnErrorDocumentOpen_Async(bool hasAgentFileOnDisk, bool isWorkspaceOpenForReads)
        {
            var workspaceDirectoryPath = isWorkspaceOpenForReads ? "file:///c:/" : null;
            await using var context = new TestHost([new McsLspModule(), new TestFileModule(hasAgentFileOnDisk)]);
            await context.InitializeLanguageServerAsync(workspaceDirectoryPath);
            var openResult = await context.OpenFileAsync("DidChange/BrokenDialog.mcs.yml");

            var expectedErrorMessage = hasAgentFileOnDisk && isWorkspaceOpenForReads ? new[]
            {
                "Tool ID 'sendMessage_QZreqo' already exists. Please use a unique ID. Ambiguity (duplicate id) found on line: 13",
                "Tool ID 'sendMessage_QZreqo' already exists. Please use a unique ID. Ambiguity (duplicate id) found on line: 17",
                "Dialog with id 'cr36e_fluxCopilot.topic.Escalate' not found",
                "Tool ID '5aXj5M' already exists. Please use a unique ID. Ambiguity (duplicate id) found on line: 22",
                "Tool ID '5aXj5M' already exists. Please use a unique ID. Ambiguity (duplicate id) found on line: 26"
            } : [];

            TestAssert.StringArrayEqual(expectedErrorMessage, openResult.Diagnostics.Where(x => x.Severity < DiagnosticSeverity.Warning).Select(x => x.Message.ReplaceLineEndings(" ")).ToArray());
        }

        [Fact]
        public async Task SuccessDiagnostics_OnDocumentChange_Async()
        {
            await using var context = new TestHost();
            await context.InitializeLanguageServerAsync();

            // Open a valid document, expect no diagnostics.
            {
                var openResult = await context.OpenFileAsync();
                Assert.Empty(openResult.Diagnostics.Where(x => x.Severity < DiagnosticSeverity.Warning));
            }

            // Introduce an error, expect diagnostics on change.
            var change = ChangeEvents.InsertTextAt("2", 14, 24);
            var changeResponse = await context.ChangeFileAsync(change) as LspJsonRpcMessage;
            var changeResult = JsonRpc.GetValidParams<DiagnosticsParams>(changeResponse);
            var diag = Assert.Single(changeResult.Diagnostics.Where(x => x.Severity < DiagnosticSeverity.Warning));
            Assert.Equal(DiagnosticSeverity.Error, diag.Severity);
            Assert.Equal("Node is unknown to the system", diag.Message);

            // Fix the error, expect diagnotics is removed.
            change = ChangeEvents.EraseCharacterAt(14, 24);
            changeResponse = await context.ChangeFileAsync(change) as LspJsonRpcMessage;
            changeResult = JsonRpc.GetValidParams<DiagnosticsParams>(changeResponse);
            Assert.Empty(changeResult.Diagnostics.Where(x => x.Severity < DiagnosticSeverity.Warning));
        }

        [Fact]
        public async Task Fail_OnInvalidMethodName_Async()
        {
            await using var context = new TestHost();
            var @params = new Dictionary<string, string>
                {
                    { "Hello", "Hi" },
                    { "Goodbye", "Farewell" }
                };
            var message = JsonRpc.CreateMessage("not/a_method", @params);
            context.TestStream.WriteMessage(message);
            await context.TestStream.CompleteProcessingAsync();
            Assert.False(context.TestStream.TryReadMessage(out _));
            Assert.StartsWith("Method 'not/a_method' is not registered.", context.Logs.Warning.Single());
        }

        public static IEnumerable<object?[]> DocumentIdentifierTestData =>
        new List<object?[]>
        {
            new object?[] { null, "default" },
            new object?[] { YamlDocumentIdentifier, "Yaml" }
        };

        private static readonly TextDocumentPositionParams YamlDocumentIdentifier = new TextDocumentPositionParams() { TextDocument = new() { Uri = new System.Uri("file:///c:/new.yml") } };

        /// <summary>
        /// Assert no crash on missing method when another language implements the method.
        /// </summary>
        [Theory]
        [MemberData(nameof(DocumentIdentifierTestData))]
        public async Task Warning_OnMissingMethodImplementation_Async(TextDocumentPositionParams? docParams, string language)
        {
            await using var context = new TestHost([new McsLspModule(), new YamlLspModule()]);
            await context.InitializeLanguageServerAsync();
            var message = JsonRpc.CreateRequestMessage(LspMethods.GoToDefinition, docParams);
            context.TestStream.WriteMessage(message);
            await context.TestStream.CompleteProcessingAsync();
            Assert.False(context.TestStream.TryReadMessage(out _));
            Assert.Empty(context.Logs.Error);
            var warning = context.Logs.Warning.Single();
            Assert.Equal($"Method 'textDocument/definition' is not implemented for language '{language}'. Cancelling request.", warning);
        }

        /// <summary>
        /// Test data for unsupported URI schemes that should resolve to default language.
        /// </summary>
        public static IEnumerable<object[]> UnsupportedSchemeTestData =>
        new List<object[]>
        {
            new object[] { "untitled:/c%3A/new.yml", "untitled" },
            new object[] { "git://repo/path/file.mcs", "git" },
            new object[] { "ssh://host/path/file.mcs", "ssh" },
            new object[] { "vscode-notebook://workspace/notebook.ipynb", "vscode-notebook" },
            new object[] { "vscode-notebook-cell://workspace/cell.py", "vscode-notebook-cell" },
            new object[] { "merge-conflict.conflict-diff://path/file.cs", "merge-conflict.conflict-diff" },
            new object[] { "vscode-remote://host/path/file.cs", "vscode-remote" },
            new object[] { "vscode-interactive://session/cell", "vscode-interactive" }
        };

        /// <summary>
        /// Requests for unsupported URI schemes should resolve to the default language.
        /// </summary>
        [Theory]
        [MemberData(nameof(UnsupportedSchemeTestData))]
        public async Task UnsupportedUriSchemes_ResolveToDefaultLanguage_Async(string uriString, string expectedScheme)
        {
            await using var context = new TestHost([new McsLspModule(), new YamlLspModule()]);
            await context.InitializeLanguageServerAsync();

            var docParams = new TextDocumentPositionParams
            {
                TextDocument = new() { Uri = new System.Uri(uriString) }
            };

            var message = JsonRpc.CreateRequestMessage(LspMethods.GoToDefinition, docParams);
            context.TestStream.WriteMessage(message);
            await context.TestStream.CompleteProcessingAsync();

            Assert.False(context.TestStream.TryReadMessage(out _));
            Assert.Empty(context.Logs.Error);

            var warning = context.Logs.Warning.Single();
            Assert.Contains("textDocument/definition", warning);
            Assert.Contains("default", warning, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("not implemented", warning, StringComparison.OrdinalIgnoreCase);
            // Use expectedScheme by ensuring the original URI's scheme matches test data (guards against stale data)
            var actualScheme = new System.Uri(uriString).Scheme;
            Assert.Equal(expectedScheme, actualScheme);
        }
    }
}
