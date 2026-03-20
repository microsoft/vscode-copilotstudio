
namespace Microsoft.PowerPlatformLS.Contracts.Internal.Common.Framework
{
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models.Lsp;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;

    public interface IDiagnosticsProvider<DocType>
        where DocType : LspDocument
    {
        IEnumerable<Diagnostic> ComputeDiagnostics(RequestContext requestContext, DocType document);
    }
}
