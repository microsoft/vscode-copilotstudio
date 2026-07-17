namespace Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Handlers
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.Agents.ObjectModel.Syntax;
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Completion;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Models;
    using System.Threading;
    using System.Threading.Tasks;

    [LspMethodHandler(LspMethods.GoToDefinition)]
    internal class GoToDefinitionHandler : IRequestHandler<TextDocumentPositionParams, Location?, RequestContext>
    {
        private readonly ILspLogger _logger;
        private readonly IGlobalVariableReferenceService _globalVariableReferenceService;
        private readonly IReferenceResolver? _refResolver;

        public GoToDefinitionHandler(ILspLogger lspLogger, IGlobalVariableReferenceService globalVariableReferenceService, IReferenceResolver? refResolver = null)
        {
            _logger = lspLogger;
            _globalVariableReferenceService = globalVariableReferenceService;
            _refResolver = refResolver;
        }

        public bool MutatesSolutionState => false;

        public Task<Location?> HandleRequestAsync(TextDocumentPositionParams request, RequestContext context, CancellationToken cancellationToken)
        {
            if (_globalVariableReferenceService.TryResolveIdentityAtPosition(context, out var identity) && _globalVariableReferenceService.TryGetDefinitionUri(context, identity, out var definitionUri))
            {
                return Task.FromResult<Location?>(new Location
                {
                    Uri = definitionUri,
                    Range = Range.Zero,
                });
            }

            var syntax = HandleRequest(request, context, cancellationToken);

            Location? location = null;
            if (syntax != null)
            {
                location = new Location
                {
                    Uri = syntax.SourceUri,
                    Range = Range.Zero,
                };
            }

            return Task.FromResult(location);
        }

        private SyntaxNode? HandleRequest(TextDocumentPositionParams request, RequestContext context, CancellationToken cancellationToken)
        {
            var document = context.Document.As<McsLspDocument>();
            var positionIndex = document.MarkResolver.GetIndex(request.Position);
            var docElement = document.FileModel;
            if (docElement == null || docElement.Syntax == null)
            {
                return null;
            }

            BotElement? elementAtPosition;
            try
            {
                // Need semantics for TryResolveTargetDialog to work. 
                elementAtPosition = context.GetCurrentElement();
            }
            catch (ArgumentOutOfRangeException)
            {
                _logger.LogError($"Unexpected error in {nameof(GoToDefinitionHandler)}. Invalid request. Position is out of range.");
                return null;
            }

            // For component collections references. In 'references.mcs.yml', goto the target 'collection.mcs.yml'
            if (elementAtPosition is ReferenceItemSourceFile ref1 && _refResolver != null)
            {
                var dir = ref1.Directory;
                var workspacePath = context.Workspace.FolderPath;

                try
                {
                    var cc = _refResolver.ResolveComponentCollectionOrThrow(workspacePath, ref1);
                    var syntax = cc.ComponentCollection?.Syntax;
                    return syntax;
                }
                catch
                {
                    // This just means goto-definition won't work.
                    // User should likely see errors in IDE already. 
                    return null;
                }
            }

            // WIP : Adding more references types
            // use nested ifs instead of && to ease future extensibility
            if (elementAtPosition is DialogExpression dialogExpression)
            {
                if (dialogExpression.IsLiteral)
                {
                    if (dialogExpression.Parent is BaseInvokeDialog dialog)
                    {
                        if (dialog.TryResolveTargetDialog(out var targetDialog))
                        {
                            var syntax = targetDialog.Syntax;
                            return syntax;
                        }
                    }
                }
            }

            return null;
        }
    }
}
