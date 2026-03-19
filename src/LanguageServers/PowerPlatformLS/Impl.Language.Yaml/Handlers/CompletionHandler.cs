
namespace Microsoft.PowerPlatformLS.Impl.Language.Yaml.Handlers
{
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Completion;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using System.Threading;
    using System.Threading.Tasks;

    [LspMethodHandler(LspMethods.Completion)]
    internal class CompletionHandler : IRequestHandler<CompletionParams, CompletionList, RequestContext>
    {
        private readonly ICompletionRulesProcessor<YamlLspDocument> _completionRules;

        public CompletionHandler(ICompletionRulesProcessor<YamlLspDocument> completionRules)
        {
            _completionRules = completionRules;
        }

        public bool MutatesSolutionState => false;

        public Task<CompletionList> HandleRequestAsync(CompletionParams request, RequestContext context, CancellationToken cancellationToken)
        {
            var result = new CompletionList();
            var document = context.Document.As<YamlLspDocument>();
            if (document.FileModel != null)
            {
                var completionItems = _completionRules.Run(context, request.Context);
                if (completionItems.Any())
                {
                    result.Items = completionItems.ToArray();
                }
            }

            return Task.FromResult(result);
        }
    }
}
