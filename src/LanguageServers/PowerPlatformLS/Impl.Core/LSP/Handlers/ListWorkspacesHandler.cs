namespace Microsoft.PowerPlatformLS.Impl.Core.Lsp.Handlers
{
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Impl.Core.LSP.Models;
    using System;
    using System.Collections.Immutable;
    using System.Threading;
    using System.Threading.Tasks;

    [LanguageServerEndpoint(LspMethods.ListWorkspaces, LanguageServerConstants.DefaultLanguageName)]
    internal class ListWorkspacesHandler : IRequestHandler<ListWorkspacesResponse, RequestContext>
    {
        private readonly ILanguageProvider _languageProvider;
        private readonly ClientInformation _clientInfo;
        private readonly ILspLogger _logger;

        public ListWorkspacesHandler(ILanguageProvider languageProvider, ClientInformation clientInfo, ILspLogger logger)
        {
            _languageProvider = languageProvider ?? throw new ArgumentNullException(nameof(languageProvider));
            _clientInfo = clientInfo ?? throw new ArgumentNullException(nameof(clientInfo));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public bool MutatesSolutionState => false;

        public Task<ListWorkspacesResponse> HandleRequestAsync(RequestContext context, CancellationToken cancellationToken)
        {
            var workspaceUris = new HashSet<Uri>();

            if (_languageProvider.TryGetLanguage(LanguageType.CopilotStudio, out var language))
            {
                foreach (var ws in language.Workspaces)
                {
                    if (!string.IsNullOrWhiteSpace(ws.FolderPath.ToString()))
                    {
                        workspaceUris.Add(new Uri(ws.FolderPath.ToString()));
                    }
                }

                foreach (var rootFolder in _clientInfo.WorkspaceFolders)
                {
                    if (!Directory.Exists(rootFolder.ToString()))
                    {
                        continue;
                    }

                    ScanForAgents(rootFolder, language, workspaceUris);
                }
            }

            var response = new ListWorkspacesResponse
            {
                WorkspaceUris = workspaceUris.ToImmutableArray()
            };

            return Task.FromResult(response);
        }

        private void ScanForAgents(DirectoryPath currentFolder, ILanguageAbstraction language, HashSet<Uri> workspaceUris)
        {
            if (workspaceUris.Contains(new Uri(currentFolder.ToString())))
            {
                return;
            }

            if (language.IsValidAgentDirectory(currentFolder, out var validDirectory))
            {
                workspaceUris.Add(new Uri(validDirectory.ToString()));
                return;
            }

            try
            {
                foreach (var dir in Directory.EnumerateDirectories(currentFolder.ToString(), "*", SearchOption.TopDirectoryOnly))
                {
                    ScanForAgents(new DirectoryPath(dir.Replace('\\', '/')), language, workspaceUris);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error scanning directory for list of workspaces: {ex.Message}");
            }
        }
    }
}
