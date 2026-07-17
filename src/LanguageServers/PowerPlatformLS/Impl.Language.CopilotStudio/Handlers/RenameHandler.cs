namespace Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Handlers
{
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using System;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    [LspMethodHandler(LspMethods.Rename)]
    internal class RenameHandler : IRequestHandler<RenameParams, WorkspaceEdit?, RequestContext>
    {
        private readonly IGlobalVariableReferenceService _referenceService;

        public RenameHandler(IGlobalVariableReferenceService referenceService)
        {
            _referenceService = referenceService;
        }

        public bool MutatesSolutionState => false;

        public Task<WorkspaceEdit?> HandleRequestAsync(RenameParams request, RequestContext context, CancellationToken cancellationToken)
        {
            var newName = request.NewName?.Trim();
            if (!RenameEditFactory.IsValidNewName(newName) || !_referenceService.TryResolveIdentityAtPosition(context, out var identity))
            {
                return Task.FromResult<WorkspaceEdit?>(null);
            }

            if (string.Equals(newName, identity.VariableName, StringComparison.Ordinal))
            {
                return Task.FromResult<WorkspaceEdit?>(null);
            }

            if (_referenceService.GetGlobalVariableNames(context).Any(existingName => !string.Equals(existingName, identity.VariableName, StringComparison.OrdinalIgnoreCase) && string.Equals(existingName, newName, StringComparison.OrdinalIgnoreCase)))
            {
                return Task.FromException<WorkspaceEdit?>(new InvalidOperationException($"A global variable named '{newName}' already exists. Choose a different name."));
            }

            var references = _referenceService.FindReferences(context, identity);
            if (references.Count == 0)
            {
                return Task.FromResult<WorkspaceEdit?>(null);
            }

            var definitionUri = _referenceService.TryGetDefinitionUri(context, identity, out var resolvedUri) ? resolvedUri : null;
            return Task.FromResult<WorkspaceEdit?>(RenameEditFactory.Build(references, newName!, definitionUri));
        }
    }
}
