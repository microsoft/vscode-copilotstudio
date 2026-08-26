
namespace Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Handlers
{
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common.Handlers;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Models;

    [LspMethodHandler(LspMethods.DidChange)]
    class DidChangeHandler : BaseDidChangeMethodHandler<McsLspDocument>
    {
        public DidChangeHandler(IDiagnosticsPublisher publisher, ILspLogger logger)
            : base(publisher, logger)
        {
        }
    }
}
