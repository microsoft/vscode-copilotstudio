namespace Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Models
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.Agents.ObjectModel.Abstractions;
    using Microsoft.Agents.ObjectModel.Analysis;
    using Microsoft.Agents.ObjectModel.Expressions;
    using Microsoft.Agents.ObjectModel.PowerFx;
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.Extensions.FileProviders;
    using Microsoft.PowerPlatformLS.Contracts.FileLayout;
    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common.Framework;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models.Lsp;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using System;
    using System.Globalization;

    /// <summary>
    /// Represents an agent directory, which is an isolated project scope.
    /// An agent directory is compiled as a single entity (BotDefinition).
    /// </summary>
    internal class McsWorkspace : Workspace, IMcsWorkspace
    {
        private readonly IDiagnosticsProvider<McsLspDocument> _diagnosticProvider;
        private readonly ILspLogger _logger;
        private readonly IWorkspaceCompiler<DefinitionBase> _workspaceCompiler;
        private readonly PowerFxExpressionChecker _expressionChecker;
        private readonly IFeatureConfiguration _featureConfiguration;

        public McsWorkspace(DirectoryPath workspaceFolderPath, ILspServices lspServices) : base(workspaceFolderPath)
        {
            _diagnosticProvider = lspServices.GetRequiredService<IDiagnosticsProvider<McsLspDocument>>();
            _logger = lspServices.GetRequiredService<ILspLogger>();
            _workspaceCompiler = lspServices.GetRequiredService<IWorkspaceCompiler<DefinitionBase>>();
            _featureConfiguration = lspServices.GetRequiredService<IFeatureConfiguration>();
            _expressionChecker = new(_featureConfiguration);
        }

        /// <summary>
        /// Get the expression checker for validating Power Fx expressions.
        /// </summary>
        public IExpressionChecker ExpressionChecker => _expressionChecker;

        public IFeatureConfiguration FeatureConfiguration => _featureConfiguration;

        /// <summary>
        /// The compilation model is the equivalent of a "Compilation":
        /// it's a model that contains all the documents in the workspace and their semantic model.
        /// </summary>
        public McsCompilationAnalyzer? CompilationAnalyzer { get; private set; }

        public McsCompilationAnalyzer RequiredCompliationAnalyzer => CompilationAnalyzer ?? throw new InvalidOperationException($"Missing {nameof(CompilationAnalyzer)}.");

        public DefinitionBase Definition 
        {
            get
            {
                return CompilationAnalyzer?.RootDefinition ?? throw new InvalidOperationException($"Call BuildCompilationModel()");
            }
        }

        public override void AddDocument(LspDocument document)
        {
            base.AddDocument(document);

            int len = document.Text.Length;
            _logger.LogInformation($"Document {document.Uri.AbsoluteUri}, len={len}, tracked in current workspace.");
        }

        public IExpressionCheckerOperationContext GetOperationContext()
        {
            var compilationModel = CompilationAnalyzer;

            if (compilationModel == null)
            {
                throw new InvalidOperationException($"Compilation should have been computed");
            }

            IExpressionCheckerOperationContext operationContext = compilationModel.RootDefinition.GetCheckerContext();

            return operationContext;
        }

        public SemanticModel GetSemanticModel(BotElement botElement)
        {
            var semanticModel = botElement.GetSemanticModel(ExpressionChecker, FeatureConfiguration);
            return semanticModel;
        }

        public override void BuildCompilationModel()
        {
            base.BuildCompilationModel();

            var compilation = _workspaceCompiler.Compile(
                _documents,
                FolderPath);

            CompilationAnalyzer = new McsCompilationAnalyzer(_documents, compilation.Model, compilation.Errors);
        }

        public override bool UpdateDocument(RequestContext context, TextDocumentChangeEvent[] changes)
        {
            // TODO: Consider updating the document in place instead of removing and re-adding it.
            // Consider updating the document in place instead of removing and re-adding it.
            // i.e. Update existing file and workspace compilation Model.
            if (!context.Document.ApplyChanges(changes))
            {
                return false;
            }

            BuildCompilationModel();
            return true;
        }

        /// <inheritdoc/>
        public override IEnumerable<DiagnosticsParams> GetDiagnostics(RequestContext requestContext)
        {
            foreach (var document in _documents.Values)
            {
                // don't use CompilationAnalyzer directly as it doesn't include all diagnostics sources
                var diagnostics = _diagnosticProvider.ComputeDiagnostics(requestContext, document.As<McsLspDocument>());
                var diag = new DiagnosticsParams
                {
                    Uri = document.Uri,
                    Diagnostics = diagnostics.ToArray(),
                };
                yield return diag;
            }
        }

        public override LspDocument UpsertDocumentFromFile(FilePath documentPath, IFileInfo fileInfo, ILanguageAbstraction language, CultureInfo cultureInfo)
        {
            // in the future, we could decide that icons are not documents and track them otherwise,
            // but for now, we treat them as such.
            if (AgentFilePath.IsIcon(documentPath))
            {
                return GetOrCreateDocument(documentPath, fileInfo.ReadBase64(), language, cultureInfo);
            }

            return base.UpsertDocumentFromFile(documentPath, fileInfo, language, cultureInfo);
        }

        public LspDocument? GetDocument(AgentFilePath path)
        {
            var fullPath = FolderPath.ToString() + path.ToString();

            return GetDocument(new FilePath(fullPath));
        }
    }
}
