namespace Microsoft.PowerPlatformLS.Impl.Language.Yaml.Framework
{
    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common.Framework;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models.Lsp;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Validation;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using System.Collections.Generic;

    internal class DiagnosticsProvider : IDiagnosticsProvider<YamlLspDocument>
    {
        private readonly IValidationRulesProcessor<YamlLspDocument> _validationRules;

        public DiagnosticsProvider(IValidationRulesProcessor<YamlLspDocument> validationRules)
        {
            _validationRules = validationRules;
        }

        public IEnumerable<Diagnostic> ComputeDiagnostics(RequestContext requestContext, YamlLspDocument document)
        {
            if (document.IsWorkspaceLayoutMarker)
            {
                return [];
            }

            var semanticModel = document.FileModel;

            if (semanticModel == null)
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
