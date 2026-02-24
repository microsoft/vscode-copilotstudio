namespace Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.SemanticToken
{
    using Microsoft.Agents.ObjectModel.Syntax;
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;

    internal static class SemanticTokenHelper
    {
        public static int[] GetSemanticTokenData(SyntaxNode? syntaxNode, RequestContext requestContext, ILspLogger logger)
        {
            var semanticTokenData = Array.Empty<int>();
            if (syntaxNode != null)
            {
                var semanticTokenVisitor = new SemanticTokenVisitor(requestContext, logger);
                semanticTokenVisitor.Visit(syntaxNode);                
                semanticTokenData = semanticTokenVisitor.SemanticTokenData.ToArray();
            }

            return semanticTokenData;
        }
    }
}
