namespace Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Exceptions
{
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models.Lsp;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.Agents.ObjectModel;

    internal class BadReferenceException : McsException
    {
        // Item provides syntax for better range.
        public BadReferenceException(string? message, ReferenceItemSourceFile item) : base(message, DiagnosticSeverity.Error)
        {
        }

        protected override DiagnosticData GetDiagnosticData(LspDocument document, RequestContext context)
        {
            return new DiagnosticData();
        }
    }
}
