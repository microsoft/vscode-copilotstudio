namespace Microsoft.PowerPlatformLS.Impl.Core.Lsp.Handlers
{
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    [LanguageServerEndpoint(LspMethods.CodeAction, LanguageServerConstants.DefaultLanguageName)]
    internal class CodeActionHandler : IRequestHandler<CodeActionParams, CodeAction[], RequestContext>
    {
        public bool MutatesSolutionState => false;

        public Task<CodeAction[]> HandleRequestAsync(CodeActionParams request, RequestContext context, CancellationToken cancellationToken)
        {
            // avoid unnecessary allocations when there is only one diagnostic (most common case)
            CodeAction[] codeActions = request.Context.Diagnostics.Length == 1 ?
                request.Context.Diagnostics[0].Data?.Quickfix ?? Array.Empty<CodeAction>() :
                request.Context.Diagnostics.SelectMany(diag => diag.Data?.Quickfix ?? []).ToArray();
            return Task.FromResult(codeActions);
        }
    }
}
