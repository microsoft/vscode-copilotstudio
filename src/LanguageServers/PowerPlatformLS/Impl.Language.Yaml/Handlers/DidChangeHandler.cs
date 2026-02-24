
namespace Microsoft.PowerPlatformLS.Impl.Language.Yaml.Handlers
{
    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common.Framework;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common.Handlers;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;

    [LspMethodHandler(LspMethods.DidChange)]
    class DidChangeHandler : BaseDidChangeMethodHandler<YamlLspDocument>
    {
        public DidChangeHandler(IDiagnosticsPublisher publisher)
            : base(publisher)
        {
        }
    }
}
