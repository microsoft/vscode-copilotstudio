namespace Microsoft.PowerPlatformLS.UnitTests.Impl.Language.CopilotStudio
{
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Handlers;
    using Moq;
    using System.Threading;
    using System.Threading.Tasks;
    using Xunit;
 
    public class SemanticTokenFullHandlerTests
    {
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task SemanticTokenHandleTestAsync(bool hasInvalidFirstKind)
        {
            var world = new World();
            var doc = world.AddFile("topic2.mcs.yml");
            var requestContext = world.GetRequestContext(doc, 0);

            if (hasInvalidFirstKind)
            {
                requestContext.Document.UpdateText($"kind: invalidKindValue \r\n {requestContext.Document.Text}");
            }

            var _semanticTokensParams = new SemanticTokensParams()
            {
                TextDocument = new TextDocumentIdentifier()
                {
                    Uri = requestContext.Document.Uri
                }
            };

            var semanticTokenFullHandler = new SemanticTokenFullHandler(Mock.Of<ILspLogger>());

            var result = await semanticTokenFullHandler.HandleRequestAsync(_semanticTokensParams, requestContext, CancellationToken.None);

            Assert.True(result.Data?.Length > 0);
            Assert.Equal(requestContext.Index.ToString(), result.ResultId);
        }
    }
}
