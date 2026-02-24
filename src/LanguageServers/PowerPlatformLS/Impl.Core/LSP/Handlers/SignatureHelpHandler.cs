namespace Microsoft.PowerPlatformLS.Impl.Core.Lsp.Handlers
{
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using System.Threading;
    using System.Threading.Tasks;

    [LanguageServerEndpoint(LspMethods.SignatureHelp, LanguageServerConstants.DefaultLanguageName)]
    class SignatureHelpHandler : IRequestHandler<SignatureHelpParams, SignatureHelp, RequestContext>
    {
        public bool MutatesSolutionState => false;

        public Task<SignatureHelp> HandleRequestAsync(SignatureHelpParams request, RequestContext context, CancellationToken cancellationToken)
        {
            // no signature help by default
            return Task.FromResult(new SignatureHelp());
        }
    }
}
