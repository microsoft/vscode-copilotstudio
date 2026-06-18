namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.CopilotStudio.Sync;
    using Microsoft.PowerPlatformLS.Contracts.FileLayout;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    [LanguageServerEndpoint("powerplatformls/listKnowledgeFiles", LanguageServerConstants.DefaultLanguageName)]
    internal class ListKnowledgeFilesHandler : IRequestHandler<ListKnowledgeFilesRequest, ListKnowledgeFilesResponse, RequestContext>
    {
        private readonly IWorkspaceSynchronizer _synchronizer;
        private readonly ILspLogger _logger;

        public ListKnowledgeFilesHandler(IWorkspaceSynchronizer synchronizer, ILspLogger logger)
        {
            _synchronizer = synchronizer ?? throw new ArgumentNullException(nameof(synchronizer));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public bool MutatesSolutionState => false;

        public async Task<ListKnowledgeFilesResponse> HandleRequestAsync(ListKnowledgeFilesRequest request, RequestContext context, CancellationToken cancellationToken)
        {
            try
            {
                var workspace = (IMcsWorkspace)context.Workspace;
                var files = await _synchronizer.ListKnowledgeFilesAsync(workspace.FolderPath, cancellationToken);

                return new ListKnowledgeFilesResponse
                {
                    Code = 200,
                    Files = files,
                };
            }
            catch (Exception ex)
            {
                var (code, message) = LspExceptionHandler.Handle(ex, _logger, cancellationToken);
                return new ListKnowledgeFilesResponse
                {
                    Code = code,
                    Message = message,
                };
            }
        }
    }
}
