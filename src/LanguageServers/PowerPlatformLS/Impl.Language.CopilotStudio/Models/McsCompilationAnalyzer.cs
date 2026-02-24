namespace Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Models
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.Agents.ObjectModel.Syntax;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models.Lsp;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Completion;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Exceptions;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Validation;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;

    /// <summary>
    /// MCS Bot Definition wrapper.
    /// This is combining the equivalent of a Roslyn "Compilation" and "SemanticModel".
    /// It contains all the documents in the workspace and their semantic model and their syntax tree.
    /// It also contains the BotDefinition and its components, extracted and parsed from the documents, then combined together.
    /// MCS files are not technically compiled in the traditional sense.
    /// </summary>
    internal class McsCompilationAnalyzer
    {
        private readonly IReadOnlyDictionary<LspDocument, IEnumerable<Exception>> _workspaceErrors;
        private readonly IReadOnlyDictionary<FilePath, LspDocument> _documents;
        private readonly DefinitionBase _root;

        public McsCompilationAnalyzer(
            IReadOnlyDictionary<FilePath, LspDocument> documents,
            DefinitionBase root,
            IReadOnlyDictionary<LspDocument, IEnumerable<Exception>> workspaceErrors)
        {
            _documents = documents;
            _root = root;
            _workspaceErrors = workspaceErrors;
        }

        public DefinitionBase RootDefinition => _root;

        /// <summary>
        /// Gets diagnostics for a given <see cref="McsLspDocument"/> in the current compilation context.
        /// Returns an empty list if the document is a <see cref="DefinitionBase"/> or an icon.
        /// If the document root cannot be determined, returns an informational diagnostic unless the file model is a <see cref="SourceFileElement"/>.
        /// Otherwise, collects diagnostics from the bot element tree and workspace errors.
        /// </summary>
        /// <param name="document">The <see cref="McsLspDocument"/> to analyze.</param>
        /// <param name="context">The request context for diagnostic conversion.</param>
        /// <returns>An enumerable of <see cref="Diagnostic"/> objects for the document.</returns>
        /// <remarks>
        /// Currently, when retrieving all workspace diagnostics, the method is called for each document, leading to repeated traversals of the bot element tree.
        /// Potential optimization would return all diagnostics for the entire workspace at once, instead of per document.
        /// </remarks>
        /// <code>
        /// IEnumerable{BotElement} omDiagnostics = _root.DescendantsAndSelf().Where(x => x.Diagnostics.Any());
        /// var docsDiagnostics = omDiagnostics.Select(x =>
        /// {
        /// var elementWithSyntax = x.DescendantsAndSelf(x => x is BotComponentBase).FirstOrDefault(x => x.Syntax != null);
        /// var parentDocument = FindParentDocument(elementWithSyntax);
        /// return (parentDocument, FormatDiagnostics(x, elementWithSyntax));
        /// });
        /// var workspaceDiagnostics = _workspaceErrors.Select(docToErrors => (docToErrors.Key, docToErrors.Value.Select(error => ConvertErrorToDiagnostic(docToErrors.Key, error, context))));
        /// return docsDiagnostics
        /// .Concat(workspaceDiagnostics)
        /// .GroupBy(x => x.Item1)
        /// .ToDictionary(g => g.Key,  g => g.SelectMany(x => x.Item2));
        /// </code>
        internal IEnumerable<Diagnostic> GetDiagnostics(McsLspDocument document, RequestContext context)
        {
            if (document.FileModel is DefinitionBase ||
                document.IsIcon)
            {
                return [];
            }

            BotElement? currentFileRootElement;
            try
            {
                currentFileRootElement = GetDocumentRoot(document);
            }
            catch (InvalidOperationException)
            {
                currentFileRootElement = null;
            }

            IEnumerable<Diagnostic> diagnostics;
            if (currentFileRootElement == null)
            {
                if (document.FileModel is SourceFileElement)
                {
                    // ideally, this should be rooted in the definition base.
                    diagnostics = [];
                }
                else
                {
                    diagnostics = [
                        new Diagnostic
                        {
                            Message = "Document was not compiled under the current Agent Definition.",
                            Severity = DiagnosticSeverity.Information,
                            Range = Range.Zero,
                        }
                    ];
                }
            }
            else
            {
                // attach Diagnostics of BotComponents to their child's element
                var diagnosticsRoot = currentFileRootElement.ParentOfType<BotComponentBase>() ?? currentFileRootElement;
                IEnumerable<BotElement> omDiagnostics = diagnosticsRoot.DescendantsAndSelf().Where(x => x.Diagnostics.Any());
                diagnostics = omDiagnostics.SelectMany(x => FormatDiagnostics(x, currentFileRootElement));
            }

            if (_workspaceErrors.TryGetValue(document, out var errors))
            {
                diagnostics = diagnostics.Concat(errors.Select(error => ConvertErrorToDiagnostic(document, error, context)));
            }

            return diagnostics;
        }

        private Diagnostic ConvertErrorToDiagnostic(LspDocument currentDocument, Exception error, RequestContext context)
        {
            var mcsDiagnostic = (error as McsException)?.GetDiagnostic(currentDocument, context);
            return mcsDiagnostic ?? new Diagnostic
            {
                Range = Range.Zero,
                Message = error.Message,
                Severity = DiagnosticSeverity.Error,
            };
        }

        private LspDocument FindParentDocument(BotElement? botElement)
        {
            if (botElement?.Syntax != null)
            {
                return _documents[botElement.Syntax.SourceUri.ToFilePath()];
            }

            throw new Exception($"Unable to find parent document for {botElement}.");
        }

        private IEnumerable<Diagnostic> FormatDiagnostics(BotElement botElement, BotElement? knownParent = null)
        {
            var parentDocument = FindParentDocument(knownParent ?? botElement);
            foreach (var diagnostic in botElement.Diagnostics)
            {
                foreach (var lspDiagnostic in diagnostic.ToLspDiagnostics(botElement, parentDocument.MarkResolver))
                {
                    yield return lspDiagnostic;
                }
            }
        }

        /// <summary>
        /// Returns a BotElement that properly wrapped under a <see cref="RootDefinition"/>.
        /// This is essential for symbol operations to succeed.
        /// </summary>
        /// <param name="document"></param>
        /// <returns></returns>
        public BotElement GetDocumentRoot(McsLspDocument document)
        {
            var currentFileRootElement = document.FileModel;
            BotElement wrappedNode;

            if (currentFileRootElement == null)
            {
                throw new InvalidOperationException($"FileModel is missing");
            }

            var uri = currentFileRootElement?.Syntax?.SourceUri;

            if (currentFileRootElement is SourceFileElement)
            {
                // Not yet rooted in BotDefinition. 
                return currentFileRootElement; 
            }

            if (currentFileRootElement is DefinitionBase)
            {
                wrappedNode = _root;
            }
            else
            {
                Debug.Assert(uri != null);
                wrappedNode = _root.FindElementBySyntaxUri(uri);
            }
            return wrappedNode;
        }
    }
}
