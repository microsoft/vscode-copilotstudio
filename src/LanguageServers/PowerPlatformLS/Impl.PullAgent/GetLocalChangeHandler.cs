namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.CopilotStudio.Sync;
    using Microsoft.PowerPlatformLS.Contracts.FileLayout;
    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using System.Threading;
    using System.Threading.Tasks;


    [LanguageServerEndpoint(Constants.JsonRpcMethods.GetLocalChanges, LanguageServerConstants.DefaultLanguageName)]
    internal class GetLocalChangeHandler : IRequestHandler<DiffLocalRequest, SyncAgentResponse, RequestContext>
    {
        private readonly CopilotStudio.Sync.IWorkspaceSynchronizer _workspaceSynchronizer;
        private readonly ILspLogger _logger;

        public GetLocalChangeHandler(
            CopilotStudio.Sync.IWorkspaceSynchronizer workspaceSynchronizer,
            ILspLogger logger)
        {
            _workspaceSynchronizer = workspaceSynchronizer;
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public bool MutatesSolutionState => false;

        public async Task<SyncAgentResponse> HandleRequestAsync(DiffLocalRequest request, RequestContext context, CancellationToken cancellationToken)
        {
            try
            {
                var workspace = (IMcsWorkspace)context.Workspace;
                var (_, localChanges) = await _workspaceSynchronizer.GetLocalChangesAsync(workspace.FolderPath, workspace.Definition, cancellationToken);

                return new SyncAgentResponse
                {
                    Code = 200,
                    Message = string.Empty,
                    LocalChanges = localChanges
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
    }
}
