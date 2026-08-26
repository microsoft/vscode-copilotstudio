namespace Microsoft.PowerPlatformLS.UnitTests.Impl.Language.CopilotStudio.Methods
{
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using System;
    using System.IO;
    using System.Linq;
    using System.Threading.Tasks;
    using Xunit;
    using Range = PowerPlatformLS.Contracts.Lsp.Models.Range;

    public class DidCloseMethodTests
    {
        [Fact]
        public async Task DidCloseMethod()
        {
            var workspacePath = Path.GetFullPath(Path.Combine("TestData", "Workspace", "LocalWorkspace"));
            var world = new World(workspacePath);

            var path = Path.Combine(workspacePath, "topics", "Goodbye.mcs.yml");
            var doc = world.GetDocument(path);

            var handler = world.GetRequiredServices<IMethodHandler>()
                .OfType<INotificationHandler<DidCloseTextDocumentParams, RequestContext>>()
                .First();

            Assert.False(handler.MutatesSolutionState);

            var request = new DidCloseTextDocumentParams
            {
                TextDocument = new TextDocumentIdentifier
                {
                    Uri = doc!.Uri
                }
            };
            var requestContext = world.GetRequestContext(doc, 0);

            // GetRequestContext triggers agent directory change notification
            var messageReceived = world.MessagesReceived.Single();
            var jsonRpcMessage = Assert.IsType<LspJsonRpcMessage>(messageReceived);
            Assert.Equal(Constants.JsonRpcMethods.AgentDirectoryChange, jsonRpcMessage.Method);

            // Nop, but ensure we don't crash. 
            await handler.HandleNotificationAsync(request, requestContext, default);
        }

        private static string CreateWorkspaceCopy()
        {
            var source = Path.GetFullPath(Path.Combine("TestData", "Workspace", "LocalWorkspace"));
            var destination = Path.Combine(Path.GetTempPath(), "mcs-lsp-" + Guid.NewGuid().ToString("N"), "LocalWorkspace");
            Directory.CreateDirectory(destination);
            foreach (var file in Directory.GetFiles(source, "*.*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(destination, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
            }

            return destination;
        }

        [Fact]
        public async Task DeletedFolderRemovesDocumentsUnderFolder()
        {
            // Copy the workspace so the folder can actually be removed: the handler only untracks
            // documents whose files are really gone, so a delete event over a folder that is still
            // on disk is a no-op by design.
            var workspacePath = CreateWorkspaceCopy();
            var world = new World(workspacePath);

            var path = Path.Combine(workspacePath, "topics", "Goodbye.mcs.yml");
            var doc = world.GetDocument(path);

            var handler = world.GetRequiredServices<IMethodHandler>()
                .OfType<INotificationHandler<DidChangeWatchedFilesParams, RequestContext>>()
                .First();

            var deletedFolderPath = Path.Combine(workspacePath, "topics");
            Directory.Delete(deletedFolderPath, recursive: true);

            var request = new DidChangeWatchedFilesParams
            {
                Changes = new[]
                {
                    new FileEvent
                    {
                        Uri = new Uri(deletedFolderPath),
                        Type = FileChangeType.Deleted
                    }
                }
            };

            var requestContext = world.GetRequestContext(doc, 0);
            await handler.HandleNotificationAsync(request, requestContext, default);

            Assert.True(world.MessagesReceived.Any());

            var docAfterDelete = world.GetDocument(path);
            Assert.Null(docAfterDelete);

            var messageReceived = world.MessagesReceived.FirstOrDefault();
            var jsonRpcMessage = Assert.IsType<LspJsonRpcMessage>(messageReceived);
            Assert.Equal(Constants.JsonRpcMethods.AgentDirectoryChange, jsonRpcMessage.Method);

            TryDeleteDirectory(Path.GetDirectoryName(workspacePath)!);
        }

        private static void TryDeleteDirectory(string folder)
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
            }
        }

        [Fact]
        public async Task DeletedFolderEvent_WhenFilesStillExist_KeepsDocumentsTracked()
        {
            var workspacePath = Path.GetFullPath(Path.Combine("TestData", "Workspace", "LocalWorkspace"));
            var world = new World(workspacePath);

            var path = Path.Combine(workspacePath, "topics", "Goodbye.mcs.yml");
            var doc = world.GetDocument(path);

            var handler = world.GetRequiredServices<IMethodHandler>()
                .OfType<INotificationHandler<DidChangeWatchedFilesParams, RequestContext>>()
                .First();

            var request = new DidChangeWatchedFilesParams
            {
                Changes = new[]
                {
                    new FileEvent
                    {
                        Uri = new Uri(Path.Combine(workspacePath, "topics")),
                        Type = FileChangeType.Deleted
                    }
                }
            };

            await handler.HandleNotificationAsync(request, world.GetRequestContext(doc, 0), default);

            // A sync that rewrites a component's files can raise a delete event for a folder that is
            // immediately recreated. Untracking documents whose files still exist left the client
            // editing a document the server no longer knew about, and the next keystroke threw
            // "Operating on invalid context" out of textDocument/didChange.
            Assert.NotNull(world.GetDocument(path));
        }

        [Fact]
        public async Task DidChange_ForUntrackedDocument_IsIgnoredInsteadOfThrowing()
        {
            var workspacePath = Path.GetFullPath(Path.Combine("TestData", "Workspace", "LocalWorkspace"));
            var world = new World(workspacePath);

            var path = Path.Combine(workspacePath, "topics", "Goodbye.mcs.yml");
            var doc = world.GetDocument(path);
            var workspace = world.GetWorkspace(doc!);
            var language = world.GetRequiredService<ILanguageAbstraction>();

            var handler = world.GetRequiredServices<IMethodHandler>()
                .OfType<INotificationHandler<OnDidChangeParams, RequestContext>>()
                .First();

            var request = new OnDidChangeParams
            {
                TextDocument = new VersionedTextDocumentIdentifier { Uri = doc!.Uri, Version = 1 },
                ContentChanges = new[] { new TextDocumentChangeEvent { Text = "kind: AdaptiveDialog\n" } }
            };

            // The resolver hands back a context with no document when the client edits a file the
            // server has stopped tracking. Dereferencing it used to throw an unhandled
            // InvalidDataException and fail the notification; the change is dropped instead.
            var untrackedContext = new RequestContext(language, workspace, null, 0);

            await handler.HandleNotificationAsync(request, untrackedContext, default);
        }

        [Theory]
        [InlineData("Invalid")]
        [InlineData("Maker")]
        [InlineData("Invoker")]
        public async Task DidChange_InvalidContent_DoesNotCrash(string updatedMode)
        {
            var workspacePath = Path.GetFullPath(Path.Combine("TestData", "Workspace", "LocalWorkspace"));
            var world = new World(workspacePath);

            var path = Path.Combine(workspacePath, "actions", "MSNWeather-GetForecastForToday.mcs.yml");
            var doc = world.GetDocument(path);

            var handler = world.GetRequiredServices<IMethodHandler>()
                .OfType<INotificationHandler<OnDidChangeParams, RequestContext>>()
                .First();
            
            var request = new OnDidChangeParams
            {
                TextDocument = new VersionedTextDocumentIdentifier
                {
                    Uri = doc!.Uri,
                    Version = 0
                },
                ContentChanges = new[]
                {
                    new TextDocumentChangeEvent
                    {
                        Text = updatedMode,
                        Range = new Range
                        {
                            Start = new Position { Line = 9, Character = 10 },
                            End = new Position { Line = 9, Character = 10 + updatedMode.Length }
                        },
                        RangeLength = updatedMode.Length
                    }
                }
            };

            var requestContext = world.GetRequestContext(doc!, 0);

            await handler.HandleNotificationAsync(request, requestContext, default);

            Assert.True(true);
        }
    }
}
