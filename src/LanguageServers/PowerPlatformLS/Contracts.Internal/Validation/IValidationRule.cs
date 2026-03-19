namespace Microsoft.PowerPlatformLS.Contracts.Internal.Validation
{
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models.Lsp;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;

    /// <summary>
    /// Interface for validation rules that apply to given type.
    /// </summary>
    /// <typeparam name="DocumentType">Generic type used to group validation rules together.</typeparam>
    public interface IValidationRule<DocumentType> : IValidationRule
        where DocumentType : LspDocument
    {
        IEnumerable<Diagnostic> ComputeValidation(RequestContext context, DocumentType document);
    }

    public interface IValidationRule
    {
    }
}