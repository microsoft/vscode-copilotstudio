namespace Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Validation
{
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Validation;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Models;
    using System.Collections.Generic;

    internal class BotElementDiagnosticsValidationRule : IValidationRule<McsLspDocument>
    {
        IEnumerable<Diagnostic> IValidationRule<McsLspDocument>.ComputeValidation(RequestContext context, McsLspDocument document)
        {
            var workspace = context.Workspace as McsWorkspace;
            var analyzer = workspace?.CompilationAnalyzer;
            if (analyzer == null)
            {
                throw new InvalidOperationException("Can't compute rules on null compilation model.");
            }

            return analyzer.GetDiagnostics(document, context).ToArray();
        }
    }
}
