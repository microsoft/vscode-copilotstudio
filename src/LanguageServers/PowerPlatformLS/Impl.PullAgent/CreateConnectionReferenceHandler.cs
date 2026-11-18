namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{

    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.CopilotStudio.Sync;
    using Microsoft.PowerPlatformLS.Contracts.FileLayout;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    [LanguageServerEndpoint(CreateConnectionReferenceRequest.MessageName, LanguageServerConstants.DefaultLanguageName)]
    internal class CreateConnectionReferenceHandler : IRequestHandler<CreateConnectionReferenceRequest, CreateConnectionReferenceResponse, RequestContext>
    {
        private readonly IConnectionManagementService _connectionManagementService;
        private readonly ILspLogger _logger;
        private readonly IWorkspaceSynchronizer _synchronizer;

        public bool MutatesSolutionState => true;

        public CreateConnectionReferenceHandler(
            IConnectionManagementService connectionManagementService,
            ILspLogger logger,
            IWorkspaceSynchronizer synchronizer)
        {
            _connectionManagementService = connectionManagementService ?? throw new ArgumentNullException(nameof(connectionManagementService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _synchronizer = synchronizer ?? throw new ArgumentNullException(nameof(synchronizer));
        }

        public async Task<CreateConnectionReferenceResponse> HandleRequestAsync(CreateConnectionReferenceRequest request, RequestContext context, CancellationToken cancellationToken)
        {
            try
            {
                var workspace = (IMcsWorkspace)context.Workspace;
                await ConnectionHelper.SetAgentContextAsync(request, _synchronizer, workspace);
                var classification = WorkspaceClassifier.Classify(workspace.Definition, workspace.FolderPath.ToString());

                if (!classification.Allows(SyncOperation.Push))
                {
                    return new CreateConnectionReferenceResponse()
                    {
                        Code = 400,
                        Message = AuthoringSupportGate.DescribeBlocked(classification, SyncOperation.Push),
                    };
                }

                var logicalName = await _connectionManagementService.CreateConnectionReferenceForConnectorAsync(
                    workspace.FolderPath,
                    workspace.Definition,
                    request.ConnectorInternalId,
                    cancellationToken);

                return new CreateConnectionReferenceResponse()
                {
                    Code = 200,
                    Message = string.Empty,
                    LogicalName = logicalName,
                };
            }
            catch (Exception ex)
            {
                var (code, message) = LspExceptionHandler.Handle(ex, _logger, cancellationToken);
                return new CreateConnectionReferenceResponse() { Code = code, Message = message };
            }
        }
    }
}
