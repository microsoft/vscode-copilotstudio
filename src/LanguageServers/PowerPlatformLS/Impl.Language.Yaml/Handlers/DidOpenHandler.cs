namespace Microsoft.PowerPlatformLS.Impl.Language.Yaml.Handlers
{
    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common.Handlers;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;

    [LspMethodHandler(LspMethods.DidOpen)]
    internal class DidOpenHandler : BaseDidOpenHandler<YamlLspDocument>
    {
        public DidOpenHandler(IDiagnosticsPublisher diagnosticsProvider) : base(diagnosticsProvider)
        {
        }
    }
}
