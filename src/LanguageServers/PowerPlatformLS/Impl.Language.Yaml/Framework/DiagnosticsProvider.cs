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

        private static Diagnostic GetDiagnosticFromParsingError(Exception? parsingError)
        {
            Diagnostic diag;
            if (parsingError is YamlDotNet.Core.YamlException semanticError)
            {
                var startLineIdx = (int)semanticError.Start.Line - 1;
                var startCharIdx = (int)semanticError.Start.Column - 1;
                var endLineIdx = (int)semanticError.End.Line - 1;
                var endCharId = (int)semanticError.End.Column - 1;
                diag = new Diagnostic
                {
                    Range = new Contracts.Lsp.Models.Range()
                    {
                        Start = new Position() { Line = startLineIdx, Character = startCharIdx },
                        End = new Position() { Line = endLineIdx, Character = endCharId }
                    },
                    Severity = DiagnosticSeverity.Error,
                    Message = semanticError.Message,
                };
            }
            else
            {
                diag = new Diagnostic
                {
                    Range = new Contracts.Lsp.Models.Range() { Start = new Position() { Line = 0, Character = 0, }, End = new Position() { Line = 0, Character = 0 } },
                    Severity = DiagnosticSeverity.Error,
                    Message = $"Failed to compute semantic model. Unhandled exception: {parsingError}"
                };
            }

            return diag;
        }
    }
}
