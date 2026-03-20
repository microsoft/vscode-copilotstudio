namespace Microsoft.PowerPlatformLS.Impl.Core.Lsp
{
    using Microsoft.Extensions.FileProviders;
    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models.Lsp;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Impl.Core.Lsp.Uris;
    using System.Globalization;

    internal interface IRequestContextResolver
    {
        RequestContext Resolve(TextDocumentIdentifier textDocumentId, Position? position = null);
        RequestContext Resolve(TextDocumentItem textDocument);
        RequestContext Resolve(FilePath documentPath, Position? position = null, string? createIfNotFound = null);
        RequestContext Resolve(IHasWorkspace workspaceRequest);
        RequestContext Resolve(FilePath filePath, IFileInfo fileInfo);
    }

    internal class RequestContextResolver : IRequestContextResolver
    {
        private readonly ILanguageProvider _languageProvider;
        private readonly IClientInformation _clientInfo;

        public RequestContextResolver(ILanguageProvider languageProvider, IClientInformation clientInfo)
        {
            _languageProvider = languageProvider;
            _clientInfo = clientInfo;
        }

        public RequestContext Resolve(TextDocumentItem textDocument)
        {
            var lspUri = LspUriFactory.FromUri(textDocument.Uri);
            return InternalResolve(lspUri, createIfNotFound: textDocument.Text);
        }

        public RequestContext Resolve(TextDocumentIdentifier textDocumentId, Position? position = null)
        {
            var lspUri = LspUriFactory.FromUri(textDocumentId.Uri);
            
            Func<Workspace, ILanguageAbstraction, LspDocument?> createDocument = (workspace, language) =>
            {
                var filePath = lspUri is FileLspUri fileLspUri ? fileLspUri.AsFilePathNormalized() : 
                    throw new InvalidOperationException($"Unsupported URI type for workspace operations: {lspUri.GetType().Name}");
                
                return workspace.GetDocument(filePath);
            };

            return InternalResolve(lspUri, createDocument, position);
        }

        public RequestContext Resolve(FilePath documentPath, IFileInfo fileInfo)
        {
            // Convert FilePath to LspUri and delegate to new implementation
            var fileUri = new Uri(documentPath.ToString());
            var lspUri = LspUriFactory.FromUri(fileUri);
            
            Func<Workspace, ILanguageAbstraction, LspDocument> createDocument = (workspace, language) =>
            {
                return workspace.UpsertDocumentFromFile(documentPath, fileInfo, language, _clientInfo.CultureInfo);
            };

            return InternalResolve(lspUri, createDocument, null);
        }

        public RequestContext Resolve(FilePath documentPath, Position? position = null, string? createIfNotFound = null)
        {
            // Convert FilePath to LspUri and delegate to new implementation
            var fileUri = new Uri(documentPath.ToString());
            var lspUri = LspUriFactory.FromUri(fileUri);
            
            Func<Workspace, ILanguageAbstraction, LspDocument?> createDocument = (workspace, language) =>
            {
                return createIfNotFound != null ?
                    workspace.GetOrCreateDocument(documentPath, createIfNotFound, language, _clientInfo.CultureInfo) :
                    workspace.GetDocument(documentPath);
            };

            return InternalResolve(lspUri, createDocument, position);
        }

        private RequestContext InternalResolve(LspUri documentUri, Func<Workspace, ILanguageAbstraction, LspDocument?> createDocument, Position? position)
        {
            if (!_languageProvider.TryGetLanguageForDocument(documentUri, out var language))
            {
                throw new InvalidOperationException($"InternalResolve called for unsupported URI Scheme: {documentUri.Scheme}. This should never happen - fallback logic should occur at higher level.");
            }

            // For file URIs, extract the file path for workspace operations
            var filePath = documentUri is FileLspUri fileLspUri ? fileLspUri.AsFilePathNormalized() : 
                throw new InvalidOperationException($"Unsupported URI type for workspace operations: {documentUri.GetType().Name}");
            
            var workspace = language.ResolveWorkspace(filePath);
            var document = createDocument(workspace, language);

            int index = 0;
            if (position != null)
            {
                index = document?.MarkResolver.GetIndex(position.Value) ?? -1;
            }

            return new RequestContext(language, workspace, document, index);
        }

        private RequestContext InternalResolve(LspUri documentUri, string createIfNotFound)
        {
            Func<Workspace, ILanguageAbstraction, LspDocument?> createDocument = (workspace, language) =>
            {
                var filePath = documentUri is FileLspUri fileLspUri ? fileLspUri.AsFilePathNormalized() : 
                    throw new InvalidOperationException($"Unsupported URI type for workspace operations: {documentUri.GetType().Name}");
                
                return workspace.GetOrCreateDocument(filePath, createIfNotFound, language, _clientInfo.CultureInfo);
            };

            return InternalResolve(documentUri, createDocument, null);
        }

        public RequestContext Resolve(IHasWorkspace workspaceRequest)
        {
            var uri = workspaceRequest.WorkspaceUri;
            if (uri == null)
            {
                throw new ArgumentNullException(nameof(workspaceRequest.WorkspaceUri));
            }

            // WorkspaceUri is only supported for CopilotStudio language.
            // If this changes, we need to surface language parameter in the request.
            const LanguageType DefaultLanguage = LanguageType.CopilotStudio;
            if (!_languageProvider.TryGetLanguage(DefaultLanguage, out var language))
            {
                throw new KeyNotFoundException($"No analyzer defined for the '{DefaultLanguage}' type.");
            }

            var workspace = language.ResolveWorkspace(uri.ToDirectoryPath());
            return new RequestContext(language, workspace, null, 0);
        }
    }
}