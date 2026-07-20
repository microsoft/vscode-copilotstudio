namespace Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Handlers
{
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Models;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    [LspMethodHandler(LspMethods.PrepareRename)]
    internal class PrepareRenameHandler : IRequestHandler<TextDocumentPositionParams, PrepareRenameResult?, RequestContext>
    {
        private readonly IGlobalVariableReferenceService _referenceService;

        public PrepareRenameHandler(IGlobalVariableReferenceService referenceService)
        {
            _referenceService = referenceService;
        }

        public bool MutatesSolutionState => false;

        public Task<PrepareRenameResult?> HandleRequestAsync(TextDocumentPositionParams request, RequestContext context, CancellationToken cancellationToken)
        {
            if (!_referenceService.TryResolveIdentityAtPosition(context, out var identity))
            {
                return Task.FromResult<PrepareRenameResult?>(null);
            }

            var document = context.Document.As<McsLspDocument>();
            var cursorPosition = document.MarkResolver.GetPosition(context.Index);
            var referenceAtCursor = _referenceService.FindReferences(context, identity).FirstOrDefault(reference => reference.SourceUri == document.Uri && RenameEditFactory.RangeContains(reference.Range, cursorPosition));
            if (referenceAtCursor == null)
            {
                return Task.FromResult<PrepareRenameResult?>(null);
            }

            return Task.FromResult<PrepareRenameResult?>(new PrepareRenameResult
            {
                Range = referenceAtCursor.Range,
                Placeholder = identity.VariableName,
            });
        }
    }
}
