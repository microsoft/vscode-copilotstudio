namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.PowerPlatformLS.Contracts.FileLayout;
    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using System.Threading;
    using System.Threading.Tasks;


    [LanguageServerEndpoint(Constants.JsonRpcMethods.GetLocalChanges, LanguageServerConstants.DefaultLanguageName)]
    internal class GetLocalChangeHandler : IRequestHandler<DiffLocalRequest, SyncAgentResponse, RequestContext>
    {
        private readonly IWorkspaceSynchronizer _workspaceSynchronizer;

        public GetLocalChangeHandler(IWorkspaceSynchronizer workspaceSynchronizer)
        {
            _workspaceSynchronizer = workspaceSynchronizer;
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
                return new SyncAgentResponse
                {
                    Code = 500,
                    Message = ex.Message,
                };
            }
        }
    }
}