
namespace Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Handlers
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.Agents.ObjectModel.Syntax;
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Models;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.SemanticToken;
    using System;
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
            SyntaxNode? fileSyntax = doc.FileModel?.Syntax ?? TryGetSyntax(requestContext.Document.Text, doc.Uri);

            return Task.FromResult(new SemanticTokens
            {
                ResultId = requestContext.Index.ToString(),
                Data = SemanticTokenHelper.GetSemanticTokenData(fileSyntax, requestContext, _logger)
            });
        }

        private static SyntaxNode? TryGetSyntax(string text, Uri uri)
        {
            try
            {
                return CodeSerializer.Deserialize<BotElement>(text, uri)?.Syntax;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return null;
            }
        }
    }
}