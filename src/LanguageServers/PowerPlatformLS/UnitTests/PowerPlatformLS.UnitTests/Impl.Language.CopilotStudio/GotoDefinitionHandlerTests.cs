namespace Microsoft.PowerPlatformLS.UnitTests.Impl.Language.CopilotStudio
{
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.PowerPlatformLS.Contracts.FileLayout;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Handlers;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Models;
    using System;
    using System.IO;
    using System.Linq;
    using System.Threading.Tasks;
    using Xunit;

    public class GotoDefinitionHandlerTests
    {
        // Test Goto definition in happy path case. 
        [Fact]
        public async Task HappyPathAsync()
        {
            var workspacePath = Path.GetFullPath(Path.Combine("TestData", "Workspace", "LocalWorkspace"));
            var world = new World(workspacePath);

            var path = Path.Combine(workspacePath, "topics", "Goodbye.mcs.yml");
            var doc = world.GetDocument(path);

            // Find the Dialog element. Cursor | can be anywhere in the value. 
            string search = "dialog: cree9_agent.topic.|EndofConversation";
            var reqCtx = world.GetRequestContext(doc, search);

            var handler = world.GetHandler<GoToDefinitionHandler>();

            Assert.False(handler.MutatesSolutionState);

            var request = reqCtx.GetTextDocumentPositionParams();

            var location = await handler.HandleRequestAsync(request, reqCtx, default);

            Assert.NotNull(location);

            // ! Assert
            Assert.Equal("0:0-0:0", location!.Range.ToString());            
            Assert.EndsWith("topics/EndOfConversation.mcs.yml", location.Uri.ToString());

            var doc2 = world.GetDocument(location.Uri);
            Assert.NotNull(doc2);
        }

        // Test Goto definition in happy path case. 
        [Fact]
        public async Task VariableGotoPathAsync()
        {
            var workspacePath = Path.GetFullPath(Path.Combine("TestData", "Workspace", "LocalWorkspace"));
            var world = new World(workspacePath);

            var path = Path.Combine(workspacePath, "topics", "ThankYou.mcs.yml");
            var doc = world.GetDocument(path);

            // Find the Dialog element. Cursor | can be anywhere in the value. 
            string search = "variable: Glob|al.Var1";
            var reqCtx = world.GetRequestContext(doc, search);

            var handler = world.GetHandler<GoToDefinitionHandler>();

            Assert.False(handler.MutatesSolutionState);

            var request = reqCtx.GetTextDocumentPositionParams();

            var location = await handler.HandleRequestAsync(request, reqCtx, default);

            Assert.NotNull(location);

            // ! Assert
            Assert.Equal("0:0-0:0", location!.Range.ToString());
            Assert.EndsWith("variables/Var1.mcs.yml", location.Uri.ToString());

            var doc2 = world.GetDocument(location.Uri);
            Assert.NotNull(doc2);
        }

        // Test Goto definition on an element that doesn't have a target 
        [Fact]
        public async Task NotAValidLabelAsync()
        {
            var workspacePath = Path.GetFullPath(Path.Combine("TestData", "Workspace", "LocalWorkspace"));
            var world = new World(workspacePath);

            var path = Path.Combine(workspacePath, "topics", "Goodbye.mcs.yml");
            var doc = world.GetDocument(path);

            // Select an element that is not a "Dialog:" item. 
            string search = "id: dn94|DC";
            var reqCtx = world.GetRequestContext(doc, search);

            var handler = world.GetHandler<GoToDefinitionHandler>();

            var request = reqCtx.GetTextDocumentPositionParams();

            var location = await handler.HandleRequestAsync(request, reqCtx, default);

            Assert.Null(location);
        }

        // Test Goto definition in happy path case. 
        [Fact]
        public async Task ComponentCollectionsAsync()
        {
            var dir = Path.GetFullPath(Path.Combine("TestData", "WorkspaceWithCC"));

            World world = new World(dir);
            var workspace = world.GetWorkspace(Path.Combine(dir, "Agent 111"));

            var doc = workspace.GetDocumentOrThrow(new AgentFilePath(@"topics/Greeting.mcs.yml"));

            // Find the Dialog element. Cursor | can be anywhere in the value. 
            string search = "dialog: cr924_agentMXECGF.|topic.CC_Topic1";
            var reqCtx = world.GetRequestContext(doc, search);

            var handler = world.GetHandler<GoToDefinitionHandler>();

            var request = reqCtx.GetTextDocumentPositionParams();

            var location = await handler.HandleRequestAsync(request, reqCtx, default);

            Assert.NotNull(location);

            // ! Assert
            Assert.Equal("0:0-0:0", location!.Range.ToString());
            Assert.EndsWith("MyCC333/topics/cr924_agentMXECGF.topic.CC_Topic1.mcs.yml", location.Uri.ToString());
        }

        // Test Goto definition in happy path case. 
        [Fact]
        public async Task ComponentCollections2Async()
        {
            var dir = Path.GetFullPath(Path.Combine("TestData", "WorkspaceWithCC"));

            World world = new World(dir);
            var workspace = world.GetWorkspace(Path.Combine(dir, "Agent 111"));
            var doc = workspace.GetDocumentOrThrow(new AgentFilePath(@"references.mcs.yml"));

            // Find the Dialog element. Cursor | can be anywhere in the value. 
            string search = "directory: ..|/MyCC333";
            var reqCtx = world.GetRequestContext(doc, search);

            var handler = world.GetHandler<GoToDefinitionHandler>();

            var request = reqCtx.GetTextDocumentPositionParams();

            var location = await handler.HandleRequestAsync(request, reqCtx, default);

            Assert.NotNull(location);

            // ! Assert
            Assert.Equal("0:0-0:0", location!.Range.ToString());
            Assert.EndsWith("MyCC333/collection.mcs.yml", location.Uri.ToString());
        }
    }

    static class Helpers
    {
        public static McsLspDocument GetDocumentOrThrow(this McsWorkspace workspace, AgentFilePath path)
        {
            var doc = (McsLspDocument?)workspace.GetDocument(path);

            if (doc == null)
            {
                throw new InvalidOperationException($"Path not found: {path}");
            }

            return doc;
        }
    }
}
