namespace Microsoft.PowerPlatformLS.Contracts.Internal.Completion
{
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models.Lsp;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;

    public interface ICompletionRulesProcessor<DocType> : ICompletionRulesProcessor
        where DocType : LspDocument
    {
    }

    public interface ICompletionRulesProcessor
    {
        IReadOnlySet<string> TriggerCharacters { get; }
        IEnumerable<CompletionItem> Run(RequestContext requestContext, CompletionContext? triggerContext);
    }
}