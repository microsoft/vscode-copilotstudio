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

        [Fact]
        public async Task DeletedFolderRemovesDocumentsUnderFolder()
        {
            var workspacePath = Path.GetFullPath(Path.Combine("TestData", "Workspace", "LocalWorkspace"));
            var world = new World(workspacePath);

            var path = Path.Combine(workspacePath, "topics", "Goodbye.mcs.yml");
            var doc = world.GetDocument(path);

            var handler = world.GetRequiredServices<IMethodHandler>()
                .OfType<INotificationHandler<DidChangeWatchedFilesParams, RequestContext>>()
                .First();

            var deletedFolderPath = Path.Combine(workspacePath, "topics");

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
