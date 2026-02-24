namespace Microsoft.PowerPlatformLS.Contracts.Internal.Models
{
    using Microsoft.Extensions.FileProviders;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models.Lsp;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using System;
    using System.Globalization;

    /// <summary>
    /// A workspace provides access to an active set of documents, their syntax trees and the semantic model that binds them together if it exists.
    /// The workspace is updated through LSP file watching events as well as other LSP methods that may alter the file contents.
    /// </summary>
    public class Workspace
    {
        protected readonly Dictionary<FilePath, LspDocument> _documents = new();
        private readonly DirectoryPath _folderPath;

        public DirectoryPath FolderPath => _folderPath;

        public Workspace(DirectoryPath workspaceFolderPath)
        {
            _folderPath = workspaceFolderPath;
        }

        public bool IsEmpty => _documents.Count == 0;

        public LspDocument? GetDocument(FilePath path)
        {
            _documents.TryGetValue(path, out var document);
            return document;
        }

        public LspDocument GetOrCreateDocument(FilePath path, string text, ILanguageAbstraction language, CultureInfo culture)
        {
            // In most cases (99% according to roslyn docs), the document is already in the workspace and up-to-date already.
            // This is done through workspace event notifications.
            bool isDocumentDirty = true;
            if (_documents.TryGetValue(path, out var document))
            {
                isDocumentDirty = document.UpdateText(text);
            }
            else
            {
                document = language.CreateDocument(path, text, culture, _folderPath);
                AddDocument(document);
            }

            if (isDocumentDirty)
            {
                BuildCompilationModel();
            }
            return document;
        }

        public virtual void AddDocument(LspDocument document)
        {
            _documents.Add(document.FilePath, document);
        }

        public virtual void BuildCompilationModel()
        {
        }

        public virtual bool UpdateDocument(RequestContext context, TextDocumentChangeEvent[] changes)
        {
            // TODO: Use VersionedTextDocumentIdentifier to de-duplicate events and track document version internally.
            // Use VersionedTextDocumentIdentifier to de-duplicate events and keep track of document version internally.
            return context.Document.ApplyChanges(changes);
        }

        public bool RemoveDocument(FilePath path)
        {
            var response = _documents.Remove(path);
            if (response)
            {
                // tracked file was deleted, recompute
                BuildCompilationModel();
            }

            return response;
        }

        /// <summary>
        /// Compute and emit diagnostics for all files in the workspace.
        /// </summary>
        public virtual IEnumerable<DiagnosticsParams> GetDiagnostics(RequestContext requestContext)
        {
            return [];
        }

        public virtual LspDocument UpsertDocumentFromFile(FilePath documentPath, IFileInfo fileInfo, ILanguageAbstraction language, CultureInfo cultureInfo)
            => GetOrCreateDocument(documentPath, fileInfo.ReadAllText(), language, cultureInfo);
    }
}
