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
        private readonly IReferenceResolver? _refResolver;

        public GoToDefinitionHandler(ILspLogger lspLogger, IReferenceResolver? refResolver = null)
        {
            _logger = lspLogger;
            _refResolver = refResolver;
        }

        public bool MutatesSolutionState => false;

        public Task<Location?> HandleRequestAsync(TextDocumentPositionParams request, RequestContext context, CancellationToken cancellationToken)
        {
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
            else if (elementAtPosition is SetVariable setVariable)
            {
                var schemaName = GetSchemaName(setVariable);

                if (schemaName !=  null)
                {
                    var store = setVariable.ParentOfType<IGlobalVariableSchemaNameScope>();

                    if (store != null &&
                        store.TryGetGlobalVariableBySchemaName(schemaName.Value, out var globalVariableComponent))
                    {
                        return globalVariableComponent?.Variable?.Syntax;
                    }
                }
            }

            return null;
        }

        private static GlobalVariableSchemaName? GetSchemaName(SetVariable setVariable)
        {
            // Only global variables will have mcs files for go-to-definition.
            // Only global variable will have mcs files.
            if (setVariable?.Variable?.Path.Namespace == VariableNamespace.Global)
            {
                var botDefinition = setVariable.ParentOfType<BotDefinition>();

                if (botDefinition != null && botDefinition.Entity != null)
                {
                    return new GlobalVariableSchemaName(botDefinition?.Entity?.SchemaName + ".GlobalVariableComponent." + setVariable.Variable.Path.VariableName);
                }
            }

            return null;
        }
    }
}
