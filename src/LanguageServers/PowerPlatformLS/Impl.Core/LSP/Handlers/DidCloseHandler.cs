namespace Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Handlers
{
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common.Handlers;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using System.Threading;
    using System.Threading.Tasks;

    // The document close notification is sent from the client to the server when the document got closed in the client. 
    // https://microsoft.github.io/language-server-protocol/specifications/lsp/3.17/specification/#textDocument_didClose
    [LanguageServerEndpoint(LspMethods.DidClose, LanguageServerConstants.DefaultLanguageName)]
    internal class DidCloseHandler : INotificationHandler<DidCloseTextDocumentParams, RequestContext>
    {
        public DidCloseHandler()
        {
        }

        // This will allow running handler in parallel.
        // This is safe since it's a nop that doesn't mutate any state. 
        public bool MutatesSolutionState => false;

        public Task HandleNotificationAsync(DidCloseTextDocumentParams request, RequestContext requestContext, CancellationToken cancellationToken)
        {
            // Nop.
            // But this is a mandatory message and we must handle.
            // Receiving a close notification doesn’t mean that the document was open in an editor before. 
            return Task.CompletedTask;
        }
    }
}
