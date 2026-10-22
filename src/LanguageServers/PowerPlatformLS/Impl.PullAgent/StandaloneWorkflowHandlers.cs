namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.Agents.Platform.Content;
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.CopilotStudio.Sync;
    using Microsoft.CopilotStudio.Sync.Dataverse;
    using Microsoft.PowerPlatformLS.Contracts.FileLayout;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Impl.PullAgent.Auth;
    using System;
    using System.Collections.Immutable;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    internal abstract class StandaloneWorkflowHandlerBase
    {
        private readonly IIslandControlPlaneService _islandControlPlaneService;
        private readonly ITokenManager _dataverseTokenManager;
        private readonly LspDataverseHttpClientAccessor _dataverseHttpClientAccessor;

        protected StandaloneWorkflowHandlerBase(IIslandControlPlaneService islandControlPlaneService, ITokenManager dataverseTokenManager, ISyncDataverseClient dataverseClient, LspDataverseHttpClientAccessor dataverseHttpClientAccessor, IStandaloneWorkflowSynchronizer standaloneSynchronizer, IWorkspaceSynchronizer workspaceSynchronizer, IFileAccessorFactory fileAccessorFactory, ILspLogger logger)
        {
            _islandControlPlaneService = islandControlPlaneService;
            _dataverseTokenManager = dataverseTokenManager ?? throw new ArgumentNullException(nameof(dataverseTokenManager));
            _dataverseHttpClientAccessor = dataverseHttpClientAccessor ?? throw new ArgumentNullException(nameof(dataverseHttpClientAccessor));
            WorkspaceSynchronizer = workspaceSynchronizer ?? throw new ArgumentNullException(nameof(workspaceSynchronizer));
            DataverseClient = dataverseClient ?? throw new ArgumentNullException(nameof(dataverseClient));
            StandaloneSynchronizer = standaloneSynchronizer ?? throw new ArgumentNullException(nameof(standaloneSynchronizer));
            FileAccessorFactory = fileAccessorFactory ?? throw new ArgumentNullException(nameof(fileAccessorFactory));
            Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public virtual bool MutatesSolutionState => true;

        protected ISyncDataverseClient DataverseClient { get; }

        protected IStandaloneWorkflowSynchronizer StandaloneSynchronizer { get; }

        protected IWorkspaceSynchronizer WorkspaceSynchronizer { get; }

        protected IFileAccessorFactory FileAccessorFactory { get; }

        protected ILspLogger Logger { get; }

        protected Task ConnectAsync(DataverseRequest request) =>
            ConnectionHelper.ApplyConnectionContext(_islandControlPlaneService, _dataverseTokenManager, _dataverseHttpClientAccessor, DataverseClient, request, WorkspaceSynchronizer);

        protected static StandaloneWorkflowView Describe(StandaloneWorkflowDocument workflow) => new StandaloneWorkflowView
        {
            WorkflowId = workflow.WorkflowId,
            Name = workflow.Name,
            Description = workflow.Description,
            IsActivated = workflow.IsActivated,
            ModifiedOn = workflow.ModifiedOn,
            ConnectionReferences = workflow.ConnectionReferences.ToImmutableArray(),
        };

        protected async Task<TResponse> RunAsync<TResponse>(DataverseRequest request, Func<Task<TResponse>> operation, CancellationToken cancellationToken)
            where TResponse : ResponseBase, new()
        {
            try
            {
                await ConnectAsync(request);

                return await operation().ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                var (code, message) = LspExceptionHandler.Handle(failure, Logger, cancellationToken);

                return new TResponse { Code = code, Message = message };
            }
        }
    }

    [LanguageServerEndpoint(ListStandaloneWorkflowsRequest.MessageName, LanguageServerConstants.DefaultLanguageName)]
    internal class ListStandaloneWorkflowsHandler : StandaloneWorkflowHandlerBase, IRequestHandler<ListStandaloneWorkflowsRequest, ListStandaloneWorkflowsResponse, RequestContext>
    {
        public ListStandaloneWorkflowsHandler(IIslandControlPlaneService islandControlPlaneService, ITokenManager dataverseTokenManager, ISyncDataverseClient dataverseClient, LspDataverseHttpClientAccessor dataverseHttpClientAccessor, IStandaloneWorkflowSynchronizer standaloneSynchronizer, IWorkspaceSynchronizer workspaceSynchronizer, IFileAccessorFactory fileAccessorFactory, ILspLogger logger)
            : base(islandControlPlaneService, dataverseTokenManager, dataverseClient, dataverseHttpClientAccessor, standaloneSynchronizer, workspaceSynchronizer, fileAccessorFactory, logger)
        {
        }

        public override bool MutatesSolutionState => false;

        public Task<ListStandaloneWorkflowsResponse> HandleRequestAsync(ListStandaloneWorkflowsRequest request, RequestContext context, CancellationToken cancellationToken) =>
            RunAsync(request, async () => new ListStandaloneWorkflowsResponse
            {
                Code = 200,
                Message = string.Empty,
                Workflows = (await StandaloneSynchronizer.ListAsync(request.NameFilter, request.MaximumCount, cancellationToken).ConfigureAwait(false)).Select(Describe).ToImmutableArray(),
            }, cancellationToken);
    }

    [LanguageServerEndpoint(CloneStandaloneWorkflowRequest.MessageName, LanguageServerConstants.DefaultLanguageName)]
    internal class CloneStandaloneWorkflowHandler : StandaloneWorkflowHandlerBase, IRequestHandler<CloneStandaloneWorkflowRequest, CloneStandaloneWorkflowResponse, RequestContext>
    {
        public CloneStandaloneWorkflowHandler(IIslandControlPlaneService islandControlPlaneService, ITokenManager dataverseTokenManager, ISyncDataverseClient dataverseClient, LspDataverseHttpClientAccessor dataverseHttpClientAccessor, IStandaloneWorkflowSynchronizer standaloneSynchronizer, IWorkspaceSynchronizer workspaceSynchronizer, IFileAccessorFactory fileAccessorFactory, ILspLogger logger)
            : base(islandControlPlaneService, dataverseTokenManager, dataverseClient, dataverseHttpClientAccessor, standaloneSynchronizer, workspaceSynchronizer, fileAccessorFactory, logger)
        {
        }

        public Task<CloneStandaloneWorkflowResponse> HandleRequestAsync(CloneStandaloneWorkflowRequest request, RequestContext context, CancellationToken cancellationToken) =>
            RunAsync(request, async () =>
            {
                var rootPath = request.RootFolder.ToDirectoryPath();

                if (rootPath.Length == 0)
                {
                    return new CloneStandaloneWorkflowResponse { Code = 400, Message = "Request is missing root directory" };
                }

                Directory.CreateDirectory(rootPath.ToString());

                var project = await StandaloneSynchronizer.DownloadProjectAsync(request.WorkflowId, rootPath.ToString(), request.EnvironmentInfo.EnvironmentId, cancellationToken).ConfigureAwait(false);

                await WorkspaceSynchronizer.SaveSyncInfoAsync(new DirectoryPath(project.ProjectRoot.Replace('\\', '/')), request.BuildSyncInfo(null)).ConfigureAwait(false);

                return new CloneStandaloneWorkflowResponse
                {
                    Code = 200,
                    Message = string.Empty,
                    Workflow = Describe(project.Download.Workflow),
                    ProjectFolder = project.ProjectFolder,
                    FolderName = $"{project.ProjectFolder}/{project.Download.Folder}",
                    Changed = project.Download.Changed,
                };
            }, cancellationToken);
    }

    [LanguageServerEndpoint(CreateStandaloneWorkflowRequest.MessageName, LanguageServerConstants.DefaultLanguageName)]
    internal class CreateStandaloneWorkflowHandler : IRequestHandler<CreateStandaloneWorkflowRequest, CreateStandaloneWorkflowResponse, RequestContext>
    {
        private readonly ILspLogger _logger;

        public CreateStandaloneWorkflowHandler(ILspLogger logger) => _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        public bool MutatesSolutionState => true;

        public Task<CreateStandaloneWorkflowResponse> HandleRequestAsync(CreateStandaloneWorkflowRequest request, RequestContext context, CancellationToken cancellationToken)
        {
            try
            {
                var rootPath = request.RootFolder.ToDirectoryPath();

                if (rootPath.Length == 0)
                {
                    return Task.FromResult(new CreateStandaloneWorkflowResponse { Code = 400, Message = "Request is missing root directory" });
                }

                var workflowId = Guid.NewGuid();
                Directory.CreateDirectory(rootPath.ToString());

                var project = WorkflowScaffolder.ScaffoldProject(rootPath.ToString(), request.Name, workflowId);

                return Task.FromResult(new CreateStandaloneWorkflowResponse
                {
                    Code = 200,
                    Message = string.Empty,
                    WorkflowId = workflowId,
                    ProjectFolder = project.ProjectFolder,
                    FolderName = project.FolderName,
                    WrittenFiles = project.WrittenFiles.ToImmutableArray(),
                });
            }
            catch (Exception failure)
            {
                var (code, message) = LspExceptionHandler.Handle(failure, _logger, cancellationToken);

                return Task.FromResult(new CreateStandaloneWorkflowResponse { Code = code, Message = message });
            }
        }
    }

    [LanguageServerEndpoint(DeleteStandaloneWorkflowRequest.MessageName, LanguageServerConstants.DefaultLanguageName)]
    internal class DeleteStandaloneWorkflowHandler : StandaloneWorkflowHandlerBase, IRequestHandler<DeleteStandaloneWorkflowRequest, DeleteStandaloneWorkflowResponse, RequestContext>
    {
        public DeleteStandaloneWorkflowHandler(IIslandControlPlaneService islandControlPlaneService, ITokenManager dataverseTokenManager, ISyncDataverseClient dataverseClient, LspDataverseHttpClientAccessor dataverseHttpClientAccessor, IStandaloneWorkflowSynchronizer standaloneSynchronizer, IWorkspaceSynchronizer workspaceSynchronizer, IFileAccessorFactory fileAccessorFactory, ILspLogger logger)
            : base(islandControlPlaneService, dataverseTokenManager, dataverseClient, dataverseHttpClientAccessor, standaloneSynchronizer, workspaceSynchronizer, fileAccessorFactory, logger)
        {
        }

        public Task<DeleteStandaloneWorkflowResponse> HandleRequestAsync(DeleteStandaloneWorkflowRequest request, RequestContext context, CancellationToken cancellationToken) =>
            RunAsync(request, async () => new DeleteStandaloneWorkflowResponse
            {
                Code = 200,
                Message = string.Empty,
                Deleted = await StandaloneSynchronizer.DeleteAsync(request.WorkflowId, null, cancellationToken).ConfigureAwait(false),
            }, cancellationToken);
    }

    [LanguageServerEndpoint(GetStandaloneWorkflowAgentUseRequest.MessageName, LanguageServerConstants.DefaultLanguageName)]
    internal class GetStandaloneWorkflowAgentUseHandler : StandaloneWorkflowHandlerBase, IRequestHandler<GetStandaloneWorkflowAgentUseRequest, GetStandaloneWorkflowAgentUseResponse, RequestContext>
    {
        public GetStandaloneWorkflowAgentUseHandler(IIslandControlPlaneService islandControlPlaneService, ITokenManager dataverseTokenManager, ISyncDataverseClient dataverseClient, LspDataverseHttpClientAccessor dataverseHttpClientAccessor, IStandaloneWorkflowSynchronizer standaloneSynchronizer, IWorkspaceSynchronizer workspaceSynchronizer, IFileAccessorFactory fileAccessorFactory, ILspLogger logger)
            : base(islandControlPlaneService, dataverseTokenManager, dataverseClient, dataverseHttpClientAccessor, standaloneSynchronizer, workspaceSynchronizer, fileAccessorFactory, logger)
        {
        }

        public override bool MutatesSolutionState => false;

        public Task<GetStandaloneWorkflowAgentUseResponse> HandleRequestAsync(GetStandaloneWorkflowAgentUseRequest request, RequestContext context, CancellationToken cancellationToken) =>
            RunAsync(request, async () =>
            {
                var agentUse = await StandaloneSynchronizer.GetAgentUseAsync(request.WorkflowId, cancellationToken).ConfigureAwait(false);

                return new GetStandaloneWorkflowAgentUseResponse { Code = 200, Message = string.Empty, AgentNames = agentUse.AgentNames.ToImmutableArray(), Warning = agentUse.Warning };
            }, cancellationToken);
    }
}
