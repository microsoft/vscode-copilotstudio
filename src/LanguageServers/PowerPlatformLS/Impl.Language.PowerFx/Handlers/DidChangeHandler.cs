
namespace Microsoft.PowerPlatformLS.Impl.Language.PowerFx.Handlers
{
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common.Framework;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common.Handlers;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;

    [LspMethodHandler(LspMethods.DidChange)]
    class DidChangeHandler : BaseDidChangeMethodHandler<PowerFxLspDocument>
    {
        public DidChangeHandler(IDiagnosticsPublisher publisher, ILspLogger logger)
            : base(publisher, logger)
        {
        }
    }
}
