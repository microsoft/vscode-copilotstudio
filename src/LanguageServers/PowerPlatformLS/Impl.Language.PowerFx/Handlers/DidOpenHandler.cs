namespace Microsoft.PowerPlatformLS.Impl.Language.PowerFx.Handlers
{
    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common.Handlers;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;

    [LspMethodHandler(LspMethods.DidOpen)]
    internal class DidOpenHandler : BaseDidOpenHandler<PowerFxLspDocument>
    {
        public DidOpenHandler(IDiagnosticsPublisher publisher)
            : base(publisher)
        {
        }
    }
}
