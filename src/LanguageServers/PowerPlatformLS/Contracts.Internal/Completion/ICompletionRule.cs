namespace Microsoft.PowerPlatformLS.Contracts.Internal.Completion
{
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models.Lsp;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;

    public interface ICompletionRule<DocType> : ICompletionRule
        where DocType : LspDocument
    {
    }

    public interface ICompletionRule
    {
        IEnumerable<string>? CharacterTriggers { get; }

        IEnumerable<CompletionItem> ComputeCompletion(RequestContext requestContext, CompletionContext triggerContext);
    }
}