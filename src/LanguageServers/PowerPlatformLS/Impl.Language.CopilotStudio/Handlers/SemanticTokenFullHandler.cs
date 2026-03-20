
namespace Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Handlers
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.Agents.ObjectModel.Syntax;
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Models;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.SemanticToken;
    using System.Threading;
    using System.Threading.Tasks;

    [LspMethodHandler(LspMethods.SemanticTokensFull)]
    internal class SemanticTokenFullHandler : IRequestHandler<SemanticTokensParams, SemanticTokens, RequestContext>
    {
        private readonly ILspLogger _logger;

        public SemanticTokenFullHandler(ILspLogger logger)
        {
            _logger = logger;
        }

        public bool MutatesSolutionState => false;

        public Task<SemanticTokens> HandleRequestAsync(SemanticTokensParams request, RequestContext requestContext, CancellationToken cancellationToken)
        {
            var doc = requestContext.Document.As<McsLspDocument>();
            SyntaxNode? fileSyntax = doc.FileModel?.Syntax;

            if (fileSyntax == null)
            {
                var bot = CodeSerializer.Deserialize<BotElement>(requestContext.Document.Text, doc.Uri);
                fileSyntax = bot?.Syntax;
            }


            return Task.FromResult(new SemanticTokens
            {
                ResultId = requestContext.Index.ToString(),
                Data = SemanticTokenHelper.GetSemanticTokenData(fileSyntax, requestContext, _logger)
            });
        }
    }
}