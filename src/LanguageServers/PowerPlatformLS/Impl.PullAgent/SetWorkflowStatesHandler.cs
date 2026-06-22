namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.Agents.Platform.Content.Exceptions;
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.CopilotStudio.Sync;
    using Microsoft.CopilotStudio.Sync.Dataverse;
    using Microsoft.PowerPlatformLS.Contracts.FileLayout;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Impl.PullAgent.Auth;
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;

    [LanguageServerEndpoint(SetWorkflowStatesRequest.MessageName, LanguageServerConstants.DefaultLanguageName)]
    internal class SetWorkflowStatesHandler : IRequestHandler<SetWorkflowStatesRequest, SetWorkflowStatesResponse, RequestContext>
    {
        private readonly IIslandControlPlaneService _islandControlPlaneService;
        private readonly IWorkflowActivationService _workflowActivationService;
        private readonly ITokenManager _dataverseTokenManager;
        private readonly ISyncDataverseClient _dataverseClient;
        private readonly LspDataverseHttpClientAccessor _dataverseHttpClientAccessor;
        private readonly ILspLogger _logger;

        public bool MutatesSolutionState => true;

        public SetWorkflowStatesHandler(
            IIslandControlPlaneService islandControlPlaneService,
            IWorkflowActivationService workflowActivationService,
            ITokenManager dataverseTokenManager,
            ISyncDataverseClient dataverseClient,
            LspDataverseHttpClientAccessor dataverseHttpClientAccessor,
            ILspLogger logger)
        {
            _islandControlPlaneService = islandControlPlaneService;
            _workflowActivationService = workflowActivationService ?? throw new ArgumentNullException(nameof(workflowActivationService));
            _dataverseTokenManager = dataverseTokenManager ?? throw new ArgumentNullException(nameof(dataverseTokenManager));
            _dataverseClient = dataverseClient ?? throw new ArgumentNullException(nameof(dataverseClient));
            _dataverseHttpClientAccessor = dataverseHttpClientAccessor ?? throw new ArgumentNullException(nameof(dataverseHttpClientAccessor));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<SetWorkflowStatesResponse> HandleRequestAsync(SetWorkflowStatesRequest request, RequestContext context, CancellationToken cancellationToken)
        {
            try
            {
                ConnectionHelper.ApplyConnectionContext(_islandControlPlaneService, _dataverseTokenManager, _dataverseHttpClientAccessor, _dataverseClient, request);
                var workspace = (IMcsWorkspace)context.Workspace;
                var classification = AgentClassifier.Classify(workspace.Definition, workspace.FolderPath.ToString());

                if (!classification.Allows(SyncOperation.Push))
                {
                    return new SetWorkflowStatesResponse()
                    {
                        Code = 200,
                        Succeeded = false,
                        Message = AuthoringSupportGate.DescribeBlocked(classification, SyncOperation.Push),
                    };
                }

                var activationRequests = new List<WorkflowActivationRequest>(request.Changes.Count);
                foreach (var change in request.Changes)
                {
                    if (!Guid.TryParse(change.WorkflowId, out var workflowId))
                    {
                        return new SetWorkflowStatesResponse() { Code = 200, Succeeded = false, Message = $"Invalid workflow id '{change.WorkflowId}'." };
                    }

                    activationRequests.Add(new WorkflowActivationRequest { WorkflowId = workflowId, Activate = change.Activate });
                }

                var result = await _workflowActivationService.SetWorkflowActivationsAsync(
                    workspace.FolderPath,
                    activationRequests,
                    _dataverseClient,
                    cancellationToken);

                return new SetWorkflowStatesResponse()
                {
                    Code = 200,
                    Message = result.Message ?? string.Empty,
                    Succeeded = result.Succeeded,
                    Workflows = result.Workflows,
                };
            }
            catch (DataverseBadRequestException ex)
            {
                _logger.LogException(ex);
                return new SetWorkflowStatesResponse() { Code = ex.StatusCode, Message = ex.Message };
            }
            catch (Exception ex)
            {
                var (code, message) = LspExceptionHandler.Handle(ex, _logger, cancellationToken);
                return new SetWorkflowStatesResponse() { Code = code, Message = message };
            }
        }
    }
}
