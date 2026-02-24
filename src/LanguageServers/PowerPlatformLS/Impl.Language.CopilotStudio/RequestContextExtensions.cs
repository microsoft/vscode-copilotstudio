namespace Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Completion
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.Agents.ObjectModel.Syntax;
    using Microsoft.Agents.ObjectModel.Syntax.Tokens;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Models;

    internal static class RequestContextExtensions
    {
        /// <summary>
        /// Gets the current element
        /// </summary>
        public static BotElement? GetCurrentElement(this RequestContext requestContext)
        {
            var workspace = (McsWorkspace)requestContext.Workspace;
            var doc = requestContext.Document.As<McsLspDocument>();
            var rootElement = workspace.RequiredCompliationAnalyzer.GetDocumentRoot(doc);
            var fileSyntax = rootElement.Syntax;
            if (fileSyntax == null)
            {
                return null;
            }

            // The lexer associates Newlines with the next token. But we want to view the newline as the end of a current line,
            // so backup to previous token.
            var index = requestContext.Index;
            var syntaxNodeAtCursor = fileSyntax.GetSyntaxNodeAtPosition(index) switch
            {
                SyntaxToken token when token.Kind is SyntaxTokenKind.CarriageReturnLineFeed && index > 1 => fileSyntax.GetSyntaxNodeAtPosition(index - 2),
                SyntaxToken token when token.Kind is SyntaxTokenKind.LineFeed && index > 1 => fileSyntax.GetSyntaxNodeAtPosition(index - 1),
                var other => other,
            };

            return syntaxNodeAtCursor?.GetElement();
        }
    }
}