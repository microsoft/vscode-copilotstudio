namespace Microsoft.PowerPlatformLS.Contracts.Internal.Common.Framework
{
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models.Lsp;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Validation;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;

    internal class ValidationRulesProcessor<DocType> : IValidationRulesProcessor<DocType>
        where DocType : LspDocument
    {
        private readonly IEnumerable<IValidationRule<DocType>> _rules;

        public ValidationRulesProcessor(IEnumerable<IValidationRule<DocType>> rules)
        {
            _rules = rules;
        }

        IEnumerable<Diagnostic> IValidationRulesProcessor<DocType>.Run(RequestContext context, DocType document)
        {
            return _rules.SelectMany(rule => rule.ComputeValidation(context, document)).ToArray();
        }
    }
}