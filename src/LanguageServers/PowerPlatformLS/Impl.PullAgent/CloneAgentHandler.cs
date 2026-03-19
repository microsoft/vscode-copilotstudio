namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.Agents.Platform.Content;
    using Microsoft.Agents.Platform.Content.Exceptions;
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Impl.PullAgent.Auth;
    using Microsoft.PowerPlatformLS.Impl.PullAgent.Dataverse;
    using System.Collections.Generic;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;

    // For initial clone - writing to a new directory. 
    [LanguageServerEndpoint(CloneAgentRequest.MessageName, LanguageServerConstants.DefaultLanguageName)]
    internal class CloneAgentHandler : IRequestHandler<CloneAgentRequest, CloneAgentResponse, RequestContext>
    {
        private readonly IIslandControlPlaneService _islandControlPlaneService;
        private readonly IWorkspaceSynchronizer _workspaceSynchronizer;
        private readonly ITokenManager _dataverseTokenManager;
        private readonly ILspLogger _logger;
        private readonly IOperationContextProvider _operationContextProvider;
        private readonly Func<string, string, DataverseClient> _dataverseClientFactory;

        public bool MutatesSolutionState => true;

        public CloneAgentHandler(
            IIslandControlPlaneService islandControlPlaneService,
            IWorkspaceSynchronizer workspaceSynchronizer,
            ITokenManager dataverseTokenManager,
            Func<string, string, DataverseClient> dataverseClientFactory,
            IOperationContextProvider operationContextProvider,
            ILspLogger logger)
        {
            _islandControlPlaneService = islandControlPlaneService;
            _workspaceSynchronizer = workspaceSynchronizer ?? throw new ArgumentNullException(nameof(workspaceSynchronizer));
            _dataverseTokenManager = dataverseTokenManager ?? throw new ArgumentNullException(nameof(dataverseTokenManager));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _operationContextProvider = operationContextProvider ?? throw new ArgumentNullException(nameof(operationContextProvider));
            _dataverseClientFactory = dataverseClientFactory ?? throw new ArgumentNullException(nameof(dataverseClientFactory));
        }

        private static DirectoryPath ValidateRequest(CloneAgentRequest request)
        {
            var rootPath = request.RootFolder.ToDirectoryPath();
            if (rootPath.Length == 0)
            {
                throw new InvalidOperationException($"Request is missing root directory");
            }

            var rootPathValue = rootPath.ToString();
            if (!Directory.Exists(rootPathValue))
            {
                Directory.CreateDirectory(rootPathValue);
            }

            return rootPath;
        }

        // This will copy an agent to a new directory. 
        public async Task<CloneAgentResponse> HandleRequestAsync(CloneAgentRequest request, RequestContext context, CancellationToken cancellationToken)
        {
            var referenceTracker = new ReferenceTracker();

            try
            {
                _islandControlPlaneService.SetIslandBaseEndpoint(request.EnvironmentInfo.AgentManagementUrl);
                _dataverseTokenManager.SetTokens(request.DataverseAccessToken, request.CopilotStudioAccessToken);
                var syncInfo = request.GetSyncInfo();
                var rootPath = ValidateRequest(request);
                var allOperations = await _operationContextProvider.GetAllAsync(request.GetSyncInfo(), request.Assets);

                List<DirectoryPath> touchups = new List<DirectoryPath>();
                string? agentFolderName = null;

                foreach (var operationContext in allOperations)
                {
                    string displayName;
                    switch (operationContext)
                    {
                        case BotComponentCollectionAuthoringOperationContext cc:
                            displayName = request.AgentInfo.ComponentCollections.First(c => c.Id == cc.BotComponentCollectionReference.CdsId).DisplayName;
                            break;
                        case AuthoringOperationContext:
                            displayName = request.AgentInfo.DisplayName;
                            break;
                        default:
                            return new CloneAgentResponse()
                            {
                                Code = 400,
                                Message = $"Unknown operation context type {operationContext.GetType()}."
                            };
                    }

                    string folderName = SanitizeFolderName(displayName);
                    if (string.IsNullOrWhiteSpace(folderName))
                    {
                        return new CloneAgentResponse()
                        {
                            Code = 400,
                            Message = $"Display name '{displayName}' is not valid for a folder name."
                        };
                    }

                    var folder = rootPath.GetChildDirectoryPath(folderName);
                    // $$$ Usages of Directory should be under file abstraction here. 
                    if (Directory.Exists(folder.ToString()) && (Directory.GetFiles(folder.ToString()).Any()))
                    {
                        // Folder must be empty since this is a new clone.
                        return new CloneAgentResponse()
                        {
                            Code = 400,
                            Message = $"Destination path '{folder}' already exists and is not an empty directory."
                        };
                    }
                    touchups.Add(folder);
                    if (operationContext is AuthoringOperationContext)
                    {
                        // Record main agent folder only once.
                        agentFolderName ??= folderName;
                    }

                    await _workspaceSynchronizer.SaveSyncInfoAsync(folder, syncInfo);
                    var dataverseClient = CreateDataverseClient(request.EnvironmentInfo.DataverseUrl, request.DataverseAccessToken);
                    await _workspaceSynchronizer.CloneChangesAsync(folder, referenceTracker, operationContext, dataverseClient, syncInfo.AgentId, cancellationToken);
                }

                foreach (var folder in touchups)
                {
                    await _workspaceSynchronizer.ApplyTouchupsAsync(folder, referenceTracker, cancellationToken);
                }

                return new CloneAgentResponse()
                {
                    Code = 200,
                    Message = string.Empty,
                    AgentFolderName = agentFolderName
                };
            }
            catch (DataverseBadRequestException ex)
            {
                _logger.LogException(ex);
                return new CloneAgentResponse()
                {
                    Code = ex.StatusCode,
                    Message = ex.Message
                };
            }
            catch (Exception ex)
            {
                _logger.LogException(ex);
                return new CloneAgentResponse()
                {
                    Code = -1,
                    Message = ex.Message
                };
            }
        }

        // Keep characters that are: Alphanumeric(a-z, A-Z, 0-9), Underscore(_), Hyphen(-), Space, and Unicode characters above 128.
        // Other characters in the ASCII range(0–127) that are not included above are percent-encoded.
        private static string SanitizeFolderName(string displayName)
        {
            displayName = displayName.Trim();

            // Return empty string if all characters are invalid
            bool hasValidCharacters = displayName.Any(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' || c > 128);

            if (!hasValidCharacters) return string.Empty;

            return Regex.Replace(displayName, @"[\u0000-\u007F]", match =>
            {
                char c = match.Value[0];
                if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == ' ')
                    return c.ToString();
                else
                    return "%" + ((int)c).ToString("x2");
            });
        }

        protected virtual DataverseClient CreateDataverseClient(string dataverseUrl, string accessToken)
        {
            return _dataverseClientFactory(dataverseUrl, accessToken);
        }
    }
}
