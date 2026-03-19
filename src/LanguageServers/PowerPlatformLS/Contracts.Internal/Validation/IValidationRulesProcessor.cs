namespace Microsoft.PowerPlatformLS.Contracts.Internal.Validation
{
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models.Lsp;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;

    /// <summary>
    /// Interface for validation rules processor.
    /// </summary>
    /// <typeparam name="DocType">Specify a group of rules that only applies to the given type.</typeparam>
    public interface IValidationRulesProcessor<DocType>: IValidationRulesProcessor
        where DocType : LspDocument
    {
        IEnumerable<Diagnostic> Run(RequestContext context, DocType document);
    }

    public interface IValidationRulesProcessor
    {
    }
}