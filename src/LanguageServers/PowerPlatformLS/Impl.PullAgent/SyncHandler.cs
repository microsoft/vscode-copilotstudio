namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.Agents.Platform.Content;
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.CopilotStudio.Sync;
    using Microsoft.CopilotStudio.Sync.Dataverse;
    using Microsoft.PowerPlatformLS.Contracts.FileLayout;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Impl.PullAgent.Auth;
    using System;
    using System.Collections.Immutable;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    internal abstract class SyncHandler : IRequestHandler<SyncAgentRequest, SyncAgentResponse, RequestContext>
    {
        private readonly ITokenManager _dataverseTokenManager;
        protected readonly CopilotStudio.Sync.IOperationContextProvider _operationContextProvider;
        protected readonly ILspLogger _logger;
        private readonly CopilotStudio.Sync.IIslandControlPlaneService _islandControlPlaneService;
        protected readonly CopilotStudio.Sync.IWorkspaceSynchronizer _synchronizer;
        private readonly ISyncDataverseClient _dataverseClient;
        private readonly LspDataverseHttpClientAccessor _dataverseHttpClientAccessor;
        private readonly IFileAccessorFactory _fileAccessorFactory;

        protected SyncHandler(
            CopilotStudio.Sync.IIslandControlPlaneService islandControlPlaneService,
            CopilotStudio.Sync.IWorkspaceSynchronizer agentWriter,
            ITokenManager tokenManager,
            ISyncDataverseClient dataverseClient,
            LspDataverseHttpClientAccessor dataverseHttpClientAccessor,
            CopilotStudio.Sync.IOperationContextProvider operationContextProvider,
            IStandaloneWorkflowSynchronizer standaloneSynchronizer,
            IFileAccessorFactory fileAccessorFactory,
            ILspLogger logger)
        {
            _islandControlPlaneService = islandControlPlaneService;
            _synchronizer = agentWriter ?? throw new ArgumentNullException(nameof(agentWriter));
            _dataverseTokenManager = tokenManager ?? throw new ArgumentNullException(nameof(tokenManager));
            _operationContextProvider = operationContextProvider ?? throw new ArgumentNullException(nameof(operationContextProvider));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _dataverseClient = dataverseClient ?? throw new ArgumentNullException(nameof(dataverseClient));
            _dataverseHttpClientAccessor = dataverseHttpClientAccessor ?? throw new ArgumentNullException(nameof(dataverseHttpClientAccessor));
            StandaloneSynchronizer = standaloneSynchronizer ?? throw new ArgumentNullException(nameof(standaloneSynchronizer));
            _fileAccessorFactory = fileAccessorFactory ?? throw new ArgumentNullException(nameof(fileAccessorFactory));
        }

        public bool MutatesSolutionState => true;

        protected IStandaloneWorkflowSynchronizer StandaloneSynchronizer { get; }

        public async Task<SyncAgentResponse> HandleRequestAsync(SyncAgentRequest request, RequestContext context, CancellationToken cancellationToken)
        {
            try
            {
                var workspace = (IMcsWorkspace)context.Workspace;
                if (BlocksOnUnreadableDocuments)
                {
                    ThrowIfWorkspaceUnreadable(workspace);
                }

                await ConnectionHelper.ApplyConnectionContext(_islandControlPlaneService, _dataverseTokenManager, _dataverseHttpClientAccessor, _dataverseClient, request, _synchronizer, workspace);

                var fileAccessor = _fileAccessorFactory.Create(workspace.FolderPath);

                if (WorkspaceClassifier.DetectKind(fileAccessor) == WorkspaceKind.StandaloneWorkflow)
                {
                    return await ExecuteStandaloneAsync(request, workspace, fileAccessor, cancellationToken).ConfigureAwait(false);
                }

                var persistedSyncInfo = await _synchronizer.GetSyncInfoAsync(workspace.FolderPath);
                var syncInfo = BuildEffectiveSyncInfo(persistedSyncInfo, request);
                var operationContext = await _operationContextProvider.GetAsync(syncInfo);

                var (updatedDefinition, workflowResponse, aiPromptResponse, warning) = await ExecuteAsync(request, workspace, operationContext, _dataverseClient, syncInfo, cancellationToken);
                var (_, localChanges) = await _synchronizer.GetLocalChangesAsync(workspace.FolderPath, updatedDefinition, _dataverseClient, syncInfo, cancellationToken);

                return new SyncAgentResponse
                {
                    Code = 200,
                    Message = warning,
                    LocalChanges = LocalChangeDisplay.ForWorkspace(workspace, localChanges),
                    WorkflowResponse = workflowResponse,
                    AIPromptResponse = aiPromptResponse,
                };
            }
            catch (Exception ex)
            {
                var (code, message) = LspExceptionHandler.Handle(ex, _logger, cancellationToken);
                return new SyncAgentResponse
                {
                    Code = code,
                    Message = message,
                };
            }
        }

        private static AgentSyncInfo BuildEffectiveSyncInfo(AgentSyncInfo persistedSyncInfo, DataverseRequest request) => new()
        {
            AgentId = persistedSyncInfo.AgentId,
            ComponentCollectionId = persistedSyncInfo.ComponentCollectionId,
            EnvironmentDisplayName = persistedSyncInfo.EnvironmentDisplayName,
            AuthoringShape = persistedSyncInfo.AuthoringShape,
            AccountInfo = request.AccountInfo,
            DataverseEndpoint = new Uri(request.EnvironmentInfo.DataverseUrl),
            EnvironmentId = request.EnvironmentInfo.EnvironmentId,
            AgentManagementEndpoint = new Uri(request.EnvironmentInfo.AgentManagementUrl),
            SolutionVersions = request.SolutionVersions,
        };

        /// <summary>True for operations that must refuse a workspace whose files still report errors.</summary>
        protected virtual bool BlocksOnUnreadableDocuments => false;

        private async Task<SyncAgentResponse> ExecuteStandaloneAsync(SyncAgentRequest request, IMcsWorkspace workspace, IFileAccessor fileAccessor, CancellationToken cancellationToken)
        {
            var warning = await ExecuteStandaloneAsync(request, fileAccessor, cancellationToken).ConfigureAwait(false);
            var (_, localChanges) = await _synchronizer.GetLocalChangesAsync(workspace.FolderPath, workspace.Definition, cancellationToken);

            return new SyncAgentResponse
            {
                Code = 200,
                Message = warning,
                LocalChanges = LocalChangeDisplay.ForWorkspace(workspace, localChanges),
            };
        }

        /// <summary>Runs this operation against a workspace that holds workflows and no agent.</summary>
        /// <param name="request">The sync request being served.</param>
        /// <param name="fileAccessor">The workflow workspace.</param>
        /// <param name="cancellationToken">Cancels the operation.</param>
        /// <returns>A warning to report, or an empty string.</returns>
        protected abstract Task<string> ExecuteStandaloneAsync(SyncAgentRequest request, IFileAccessor fileAccessor, CancellationToken cancellationToken);

        /// <summary>Throws when any tracked document is unreadable in a way that silently loses content, naming each offending file.</summary>
        internal static void ThrowIfWorkspaceUnreadable(IMcsWorkspace workspace)
        {
            var problems = workspace.GetUnreadableDocuments()
                .Select(entry => CopilotStudio.Sync.WorkspaceDiagnostic.FromException(entry.FilePath.ToString(), entry.Failure))
                .OrderBy(diagnostic => diagnostic.FilePath, StringComparer.Ordinal)
                .ToImmutableArray();

            if (problems.Length > 0)
            {
                throw new CopilotStudio.Sync.WorkspaceValidationException(problems);
            }
        }

        protected abstract Task<(DefinitionBase Definition, ImmutableArray<WorkflowResponse> Workflows, ImmutableArray<SyncDataverseClient.AIPromptResponse> AIPrompts, string Warning)> ExecuteAsync(
            SyncAgentRequest request,
            IMcsWorkspace workspace,
            AuthoringOperationContextBase operationContext,
            ISyncDataverseClient dataverseClient,
            AgentSyncInfo syncInfo,
            CancellationToken cancellationToken);
    }
}
