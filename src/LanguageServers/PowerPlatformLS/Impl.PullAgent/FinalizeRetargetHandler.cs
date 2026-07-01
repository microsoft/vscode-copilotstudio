namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.PowerPlatformLS.Contracts.FileLayout;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    [LanguageServerEndpoint(FinalizeRetargetRequest.MessageName, LanguageServerConstants.DefaultLanguageName)]
    internal class FinalizeRetargetHandler : IRequestHandler<FinalizeRetargetRequest, FinalizeRetargetResponse, RequestContext>
    {
        private readonly CopilotStudio.Sync.IWorkspaceRetargetService _retargetService;
        private readonly ILspLogger _logger;

        public bool MutatesSolutionState => true;

        public FinalizeRetargetHandler(CopilotStudio.Sync.IWorkspaceRetargetService retargetService, ILspLogger logger)
        {
            _retargetService = retargetService ?? throw new ArgumentNullException(nameof(retargetService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public Task<FinalizeRetargetResponse> HandleRequestAsync(FinalizeRetargetRequest request, RequestContext context, CancellationToken cancellationToken)
        {
            try
            {
                var workspace = (IMcsWorkspace)context.Workspace;
                var finalized = _retargetService.FinalizeRetarget(workspace.FolderPath, request.PushSucceeded);
                return Task.FromResult(new FinalizeRetargetResponse
                {
                    Code = 200,
                    Message = string.Empty,
                    RolledBack = finalized && !request.PushSucceeded,
                });
            }
            catch (Exception ex)
            {
                var (code, message) = LspExceptionHandler.Handle(ex, _logger, cancellationToken);
                return Task.FromResult(new FinalizeRetargetResponse { Code = code, Message = message });
            }
        }
    }

    internal class FinalizeRetargetRequest : IHasWorkspace
    {
        public const string MessageName = "powerplatformls/finalizeRetarget";

        public required Uri WorkspaceUri { get; set; }

        public bool PushSucceeded { get; set; }
    }

    internal class FinalizeRetargetResponse : ResponseBase
    {
        public bool RolledBack { get; init; }
    }
}
