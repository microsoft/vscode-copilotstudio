namespace Microsoft.PowerPlatformLS.UnitTests.Impl.Language.CopilotStudio
{
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Handlers;
    using System.IO;
    using System.Linq;
    using System.Threading.Tasks;
    using Xunit;

    public class GlobalVariableReferenceTests
    {
        private static readonly string WorkspacePath = Path.GetFullPath(Path.Combine("TestData", "Workspace", "GlobalVarWorkspace"));

        [Fact]
        public async Task FindReferences_FromSetVariableTarget_ReturnsAllSitesAsync()
        {
            var world = new World(WorkspacePath);
            var document = world.GetDocument(Path.Combine(WorkspacePath, "topics", "ThankYou.mcs.yml"))!;
            var requestContext = world.GetRequestContext(document, "variable: Glob|al.Var1");
            var handler = world.GetHandler<FindReferencesHandler>();

            Assert.False(handler.MutatesSolutionState);

            var request = ReferenceRequest(requestContext, includeDeclaration: true);
            var locations = await handler.HandleRequestAsync(request, requestContext, default);

            Assert.NotNull(locations);
            Assert.Contains(locations!, location => location.Uri.ToString().EndsWith("variables/Var1.mcs.yml"));
            Assert.Contains(locations!, location => location.Uri.ToString().EndsWith("topics/ThankYou.mcs.yml"));
            Assert.Contains(locations!, location => location.Uri.ToString().EndsWith("topics/NoNameThankYou.mcs.yml"));
            Assert.Contains(locations!, location => location.Uri.ToString().EndsWith("topics/UseGlobalVar.mcs.yml"));

            foreach (var location in locations!)
            {
                Assert.Equal("Var1", TextAt(world, location));
            }
        }

        [Fact]
        public async Task FindReferences_ExcludesDeclaration_WhenNotRequestedAsync()
        {
            var world = new World(WorkspacePath);
            var document = world.GetDocument(Path.Combine(WorkspacePath, "topics", "ThankYou.mcs.yml"))!;
            var requestContext = world.GetRequestContext(document, "variable: Glob|al.Var1");
            var handler = world.GetHandler<FindReferencesHandler>();

            var withDeclaration = await handler.HandleRequestAsync(ReferenceRequest(requestContext, includeDeclaration: true), requestContext, default);
            var withoutDeclaration = await handler.HandleRequestAsync(ReferenceRequest(requestContext, includeDeclaration: false), requestContext, default);

            Assert.Contains(withDeclaration!, location => location.Uri.ToString().EndsWith("variables/Var1.mcs.yml"));
            Assert.DoesNotContain(withoutDeclaration!, location => location.Uri.ToString().EndsWith("variables/Var1.mcs.yml"));
        }

        [Fact]
        public async Task FindReferences_FromExpressionUsage_ResolvesSameVariableAsync()
        {
            var world = new World(WorkspacePath);
            var document = world.GetDocument(Path.Combine(WorkspacePath, "topics", "UseGlobalVar.mcs.yml"))!;
            var requestContext = world.GetRequestContext(document, "=Concatenate(Global.Va|r1");
            var handler = world.GetHandler<FindReferencesHandler>();

            var locations = await handler.HandleRequestAsync(ReferenceRequest(requestContext, includeDeclaration: true), requestContext, default);

            Assert.NotNull(locations);
            Assert.Contains(locations!, location => location.Uri.ToString().EndsWith("variables/Var1.mcs.yml"));
            Assert.Contains(locations!, location => location.Uri.ToString().EndsWith("topics/UseGlobalVar.mcs.yml"));
        }

        [Fact]
        public async Task FindReferences_NotOnGlobalVariable_ReturnsNullAsync()
        {
            var world = new World(WorkspacePath);
            var document = world.GetDocument(Path.Combine(WorkspacePath, "topics", "ThankYou.mcs.yml"))!;
            var requestContext = world.GetRequestContext(document, "id: setVaria|ble_Q16YQO");
            var handler = world.GetHandler<FindReferencesHandler>();

            var locations = await handler.HandleRequestAsync(ReferenceRequest(requestContext, includeDeclaration: true), requestContext, default);

            Assert.Null(locations);
        }

        [Fact]
        public async Task FindReferences_IncludesPureAndComplexExpressionUsagesAsync()
        {
            var world = new World(WorkspacePath);
            var document = world.GetDocument(Path.Combine(WorkspacePath, "topics", "ThankYou.mcs.yml"))!;
            var requestContext = world.GetRequestContext(document, "variable: Global.Va|r1");
            var handler = world.GetHandler<FindReferencesHandler>();

            var locations = await handler.HandleRequestAsync(ReferenceRequest(requestContext, includeDeclaration: false), requestContext, default);

            var inUseGlobalVar = locations!.Where(location => location.Uri.ToString().EndsWith("topics/UseGlobalVar.mcs.yml")).ToList();
            Assert.Equal(3, inUseGlobalVar.Count);
        }

        [Fact]
        public async Task FindReferences_OnNonNameFieldInDefinition_ReturnsNullAsync()
        {
            var world = new World(WorkspacePath);
            var document = world.GetDocument(Path.Combine(WorkspacePath, "variables", "Var1.mcs.yml"))!;
            var requestContext = world.GetRequestContext(document, "scope: Us|er");
            var handler = world.GetHandler<FindReferencesHandler>();

            var locations = await handler.HandleRequestAsync(ReferenceRequest(requestContext, includeDeclaration: true), requestContext, default);

            Assert.Null(locations);
        }

        private static ReferenceParams ReferenceRequest(RequestContext requestContext, bool includeDeclaration)
        {
            var position = requestContext.GetTextDocumentPositionParams();
            return new ReferenceParams
            {
                TextDocument = position.TextDocument,
                Position = position.Position,
                Context = new ReferenceContext { IncludeDeclaration = includeDeclaration },
            };
        }

        private static string TextAt(World world, Location location)
        {
            var document = world.GetDocument(location.Uri)!;
            var start = document.MarkResolver.GetIndex(location.Range.Start);
            var end = document.MarkResolver.GetIndex(location.Range.End);
            return document.Text.Substring(start, end - start);
        }
    }
}
