namespace Microsoft.PowerPlatformLS.Impl.Core.Lsp.Handlers
{
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;

    [LanguageServerEndpoint(LspMethods.DidChangeWatchedFiles, LanguageServerConstants.DefaultLanguageName)]
    internal class DidChangeWatchedFilesHandler : INotificationHandler<DidChangeWatchedFilesParams, RequestContext>
    {
        private readonly IRequestContextResolver _contextResolver;
        private readonly ILspLogger _logger;
        private readonly IClientWorkspaceFileProvider _fileProvider;
        private readonly IDiagnosticsPublisher _diagnosticPublisher;
        private readonly ILanguageProvider _languageProvider;

        public bool MutatesSolutionState => true;

        public DidChangeWatchedFilesHandler(ILspLogger logger, IRequestContextResolver contextResolver, IDiagnosticsPublisher diagnosticPublisher, IClientWorkspaceFileProvider fileProvider, ILanguageProvider languageProvider)
        {
            _contextResolver = contextResolver;
            _logger = logger;
            _fileProvider = fileProvider;
            _diagnosticPublisher = diagnosticPublisher;
            _languageProvider = languageProvider;
        }

        public async Task HandleNotificationAsync(DidChangeWatchedFilesParams request, RequestContext _1, CancellationToken cancellationToken)
        {
            var dirtyWorkspaces = new Dictionary<DirectoryPath, RequestContext>();
            foreach (var changeFileEvent in request.Changes)
            {
                // If file renaming with same chars but different casing, dont include/exclude the file from the workspace.
                if (request.ShouldIgnore(changeFileEvent))
                {
                    continue;
                }

                var filePath = changeFileEvent.Uri.ToFilePath();
                if (changeFileEvent.Type == FileChangeType.Deleted)
                {
                    if (_languageProvider.TryGetLanguage(LanguageType.CopilotStudio, out var language))
                    {
                        var workspace = language.ResolveWorkspace(filePath);

                        if (workspace.RemoveDocumentsUnderFolder(filePath))
                        {
                            dirtyWorkspaces.TryAdd(workspace.FolderPath, new RequestContext(language, workspace, null, 0));
                            continue;
                        }
                    }

                    if (!WorkspacePath.TryGetLanguageType(filePath, out _))
                    {
                        _logger.LogInformation(
                            $"Client notified '{changeFileEvent.Type}' event on watched files that has no language definition: {filePath.FileName}. Change won't be tracked.");
                        continue;
                    }

                    var context = _contextResolver.Resolve(new TextDocumentIdentifier { Uri = changeFileEvent.Uri });
                    if (context.IsInvalid)
                    {
                        _logger.LogInformation($"File is not tracked. File is not found in workspace: '{filePath.FileName}'");
                    }
                    else
                    {
                        if (context.Workspace.RemoveDocument(filePath))
                        {
                            await _diagnosticPublisher.ClearDiagnosticsAsync(changeFileEvent.Uri, cancellationToken);
                            dirtyWorkspaces.TryAdd(context.Workspace.FolderPath, context);
                        }
                    }

                    continue;
                }

                if (!WorkspacePath.TryGetLanguageType(filePath, out _))
                {
                    _logger.LogInformation(
                        $"Client notified '{changeFileEvent.Type}' event on watched files that has no language definition: {filePath.FileName}. Change won't be tracked.");
                    continue;
                }

                if (changeFileEvent.Type == FileChangeType.Created || changeFileEvent.Type == FileChangeType.Changed)
                {
                    var fileInfo = _fileProvider.GetFileInfo(filePath);
                    if (!fileInfo.Exists)
                    {
                        _logger.LogWarning(
                            $"Can't process '{changeFileEvent.Type}' event for '{filePath.FileName}': " +
                            "The file does not exist.");
                        continue;
                    }

                    var context = _contextResolver.Resolve(filePath, fileInfo);
                    if (!context.IsInvalid)
                    {
                        dirtyWorkspaces.TryAdd(context.Workspace.FolderPath, context);
                    }
                }
            }

            foreach (var workspaceContext in dirtyWorkspaces.Values)
            {
                await _diagnosticPublisher.PublishAllDiagnosticsAsync(workspaceContext, cancellationToken);
            }
        }
    }
}
