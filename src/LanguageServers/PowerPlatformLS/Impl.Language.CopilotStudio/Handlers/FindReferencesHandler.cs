namespace Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Handlers
{
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    [LspMethodHandler(LspMethods.References)]
    internal class FindReferencesHandler : IRequestHandler<ReferenceParams, Location[]?, RequestContext>
    {
        private readonly IGlobalVariableReferenceService _referenceService;

        public FindReferencesHandler(IGlobalVariableReferenceService referenceService)
        {
            _referenceService = referenceService;
        }

        public bool MutatesSolutionState => false;

        public Task<Location[]?> HandleRequestAsync(ReferenceParams request, RequestContext context, CancellationToken cancellationToken)
        {
            if (!_referenceService.TryResolveIdentityAtPosition(context, out var identity))
            {
                return Task.FromResult<Location[]?>(null);
            }

            var includeDeclaration = request.Context.IncludeDeclaration;
            var locations = _referenceService.FindReferences(context, identity).Where(reference => includeDeclaration || reference.Kind is not (GlobalVariableReferenceKind.Definition or GlobalVariableReferenceKind.DefinitionComponentName)).Select(reference => new Location { Uri = reference.SourceUri, Range = reference.Range }).ToArray();

            return Task.FromResult<Location[]?>(locations);
        }
    }
}
