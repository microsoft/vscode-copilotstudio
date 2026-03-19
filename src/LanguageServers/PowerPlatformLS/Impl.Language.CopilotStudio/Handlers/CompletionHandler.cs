
namespace Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Handlers
{
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Completion;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Models;
    using System.Threading;
    using System.Threading.Tasks;

    [LspMethodHandler(LspMethods.Completion)]
    internal class CompletionHandler : IRequestHandler<CompletionParams, CompletionList, RequestContext>
    {
        private readonly ICompletionRulesProcessor<McsLspDocument> _completionRules;

        public CompletionHandler(ICompletionRulesProcessor<McsLspDocument> completionRules)
        {
            _completionRules = completionRules;
        }

        public bool MutatesSolutionState => false;

        public Task<CompletionList> HandleRequestAsync(CompletionParams request, RequestContext context, CancellationToken cancellationToken)
        {
            return Task.FromResult(new CompletionList()
            {
                IsIncomplete = false,
                Items = _completionRules.Run(context, request.Context).ToArray(),
            });
        }
    }
}
