namespace Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Framework
{
    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common.Framework;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Validation;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Models;
    using System.Collections.Generic;

    internal class DiagnosticsProvider : IDiagnosticsProvider<McsLspDocument>
    {
        private readonly IValidationRulesProcessor<McsLspDocument> _validationRules;

        public DiagnosticsProvider(IValidationRulesProcessor<McsLspDocument> validationRules)
        {
            _validationRules = validationRules;
        }

        public IEnumerable<Diagnostic> ComputeDiagnostics(RequestContext requestContext, McsLspDocument document)
        {
            var fileModel = document.FileModel;

            // Handle error cases:
            // 1. errors with the model content (e.g. unsupported BotElement) or
            // 2. it couldn't be parsed (e.g. invalid format).
            if (document.ParsingInfo.HasError || (fileModel == null && !document.IsIcon))
            {
                return [document.ParsingInfo.Diagnostic ?? Constants.UnknownSemanticErrorDiagnostic];
            }

            var diagnostics = _validationRules.Run(requestContext, document);
            if (document.ParsingInfo.Diagnostic != null)
            {
                diagnostics = diagnostics.Append(document.ParsingInfo.Diagnostic);
            }

            return diagnostics;
        }
    }
}
