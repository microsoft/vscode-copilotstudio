namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.CopilotStudio.Sync;
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.PowerPlatformLS.Contracts.FileLayout;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using System;
    using System.Threading.Tasks;

    [LanguageServerEndpoint("powerplatformls/getWorkspaceDetails", LanguageServerConstants.DefaultLanguageName)]
    internal class GetWorkspaceDetailsHandler : IRequestHandler<GetWorkspaceDetailsParams, CopilotStudioWorkspaceInfo, RequestContext>
    {
        private readonly IFileAccessorFactory _fileAccessor;
        private readonly CopilotStudio.Sync.IWorkspaceSynchronizer _synchronizer;
        private readonly ILspLogger _logger;

        public bool MutatesSolutionState => false;

        public GetWorkspaceDetailsHandler(IFileAccessorFactory fileAccessor, CopilotStudio.Sync.IWorkspaceSynchronizer synchronizer, ILspLogger logger)
        {
            _fileAccessor = fileAccessor;
            _synchronizer = synchronizer;
            _logger = logger;
        }

        public async Task<CopilotStudioWorkspaceInfo> HandleRequestAsync(GetWorkspaceDetailsParams request, RequestContext requestContext, CancellationToken cancellationToken)
        {
            var ws = (IMcsWorkspace)requestContext.Workspace;
            var wsFolderPathValue = ws.FolderPath.ToString();
            var (displayName, description, workspaceType) = ws.Definition switch
            {
                BotDefinition bot => (bot.Entity?.DisplayName, null, WorkspaceType.Agent),
                BotComponentCollectionDefinition cc=> (cc.ComponentCollection?.DisplayName, cc.ComponentCollection?.Description, WorkspaceType.ComponentCollection),
                _ => (null, null, WorkspaceType.Unknown),
            };

            AgentSyncInfo? syncInfo = null;
            try
            {
                if (_synchronizer.IsSyncInfoAvailable(ws.FolderPath))
                {
                    syncInfo = await _synchronizer.GetSyncInfoAsync(ws.FolderPath);
                }
            }
            catch (Exception exception)
            {
                _logger.LogException(exception);
            }

            return new CopilotStudioWorkspaceInfo
            {
                WorkspaceUri = new Uri(wsFolderPathValue),
                IconFilePath = GetIconFilePath(ws),
                DisplayName = displayName ?? wsFolderPathValue.Split('/').Last(),
                Description = description,
                Type = workspaceType,
                SyncInfo = syncInfo,
            };
        }

        private string? GetIconFilePath(IMcsWorkspace ws)
        {
            var accessor = _fileAccessor.Create(ws.FolderPath);
            return accessor.Exists(new AgentFilePath("icon.png")) ? (ws.FolderPath.ToString() + "icon.png") : null;
        }
    }

    internal class GetWorkspaceDetailsParams : IHasWorkspace
    {
        public required Uri WorkspaceUri { get; set; }
    }
}
