
namespace Microsoft.PowerPlatformLS.UnitTests.TestUtilities
{
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using System;
    using System.Threading.Tasks;
    using Xunit;

    internal static class TestHostExtensions
    {
        // language configured for tests typically supports yml extension
        private static readonly Uri DefaultTestUri = new Uri("file:///c:/topics/file.mcs.yml");

        public static void SaveDocument(this TestHost context, Uri? documentUri = null, string? text = null)
        {
            var didSaveParams = new DidSaveTextDocumentParams
            {
                TextDocument = new TextDocumentIdentifier
                {
                    Uri = documentUri ?? DefaultTestUri,
                },
                Text = text,
            };

            var didSaveMessage = JsonRpc.CreateMessage(LspMethods.DidSave, didSaveParams);
            context.TestStream.WriteMessage(didSaveMessage);
        }

        public static async Task<BaseJsonRpcMessage> ChangeFileAsync(this TestHost context, TextDocumentChangeEvent change, Uri? documentUri = null)
        {
            var onChangeParams = new OnDidChangeParams
            {
                TextDocument = new VersionedTextDocumentIdentifier
                {
                    Uri = documentUri ?? DefaultTestUri,
                    Version = 2
                },
                ContentChanges = [change],
            };

            var onChangeMessage = JsonRpc.CreateMessage(LspMethods.DidChange, onChangeParams);
            context.TestStream.WriteMessage(onChangeMessage);

            // PowerPlatformLS arbitrarily send diagnostic method after ondidchange
            return await context.GetResponseAsync([LspMethods.Diagnostics]);
        }

        public static async Task<DiagnosticsParams> OpenFileAsync(this TestHost context, string filename = "AdaptiveDialog.mcs.yml", Uri? documentUri = null)
        {
            var didOpenParams = new OnDidOpenParams
            {
                TextDocument = new TextDocumentItem
                { Uri = documentUri ?? DefaultTestUri, Text = TestDataReader.GetTestData(filename), },
            };

            return await InternalOpenAsync(context, didOpenParams);
        }

        public static async Task<DiagnosticsParams> OpenDocumentWithTextAsync(this TestHost context, Uri documentUri, string text)
        {
            var didOpenParams = new OnDidOpenParams
            {
                TextDocument = new TextDocumentItem
                { Uri = documentUri, Text = text, },
            };
            return await InternalOpenAsync(context, didOpenParams);
        }

        private static async Task<DiagnosticsParams> InternalOpenAsync(TestHost context, OnDidOpenParams didOpenParams)
        {
            var didOpenMessage = JsonRpc.CreateMessage(LspMethods.DidOpen, didOpenParams);
            context.TestStream.WriteMessage(didOpenMessage);

            // PowerPlatformLS arbitrarily send diagnostic method after didopen
            var openResponse = await context.GetResponseAsync([LspMethods.Diagnostics]);
            var diagnosticMessage = openResponse as LspJsonRpcMessage;
            Assert.Equal(LspMethods.Diagnostics, diagnosticMessage?.Method);
            var openResult = JsonRpc.GetValidParams<DiagnosticsParams>(diagnosticMessage);
            return openResult;
        }

        public static async Task<BaseJsonRpcMessage> InitializeLanguageServerAsync(this TestHost context, string? workspaceDirectoryPath = "file:///")
        {
            WorkspaceFolder[]? workspaceFolders = null;
            if (workspaceDirectoryPath != null)
            {
                workspaceFolders = new[]
                {
                    new WorkspaceFolder
                    {
                        Name = "LocalWorkspace",
                        Uri = new Uri(workspaceDirectoryPath),
                    }
                };
            }

            var initializeParams = new InitializeParams
            {
                Capabilities = new ClientCapabilities(),
                WorkspaceFolders = workspaceFolders
            };

            var initializeMessage = JsonRpc.CreateRequestMessage(LspMethods.Initialize, initializeParams);
            context.TestStream.WriteMessage(initializeMessage);

            // return init response
            return await context.GetResponseAsync();
        }

        public static void SendCompletionRequest(this TestHost context, string? triggerCharacter = null)
        {
            var completionParams = new CompletionParams
            {
                TextDocument = new TextDocumentIdentifier()
                {
                    Uri = DefaultTestUri,
                },
                Context = new CompletionContext
                {
                    TriggerCharacter = triggerCharacter,
                    TriggerKind = triggerCharacter == null ? CompletionTriggerKind.Invoked : CompletionTriggerKind.TriggerCharacter,
                },
                Position = new Position { Line = 0, Character = 0 },
            };

            var message = JsonRpc.CreateRequestMessage(LspMethods.Completion, completionParams);

            context.TestStream.WriteMessage(message);
        }
    }
}
