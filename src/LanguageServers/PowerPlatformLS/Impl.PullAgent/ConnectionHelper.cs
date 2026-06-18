namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.CopilotStudio.Sync;
    using Microsoft.CopilotStudio.Sync.Dataverse;
    using Microsoft.PowerPlatformLS.Impl.PullAgent.Auth;
    using System;
    using System.Collections.Immutable;
    using System.Threading;
    using System.Threading.Tasks;

    internal static class ConnectionHelper
    {
        public static void ApplyConnectionContext(IIslandControlPlaneService islandControlPlaneService, ITokenManager dataverseTokenManager, LspDataverseHttpClientAccessor dataverseHttpClientAccessor, ISyncDataverseClient dataverseClient, DataverseRequest request)
        {
            islandControlPlaneService.SetConnectionContext(request.EnvironmentInfo.AgentManagementUrl, request.AccountInfo.ClusterCategory);
            dataverseTokenManager.SetTokens(request.DataverseAccessToken, request.CopilotStudioAccessToken);
            dataverseHttpClientAccessor.SetDataverseUrl(new Uri(request.EnvironmentInfo.DataverseUrl));
            dataverseClient.SetDataverseUrl(request.EnvironmentInfo.DataverseUrl);
        }

        public static AgentSyncInfo BuildDefaultSyncInfo(DataverseRequest request) => new()
        {
            AgentId = Guid.Empty,
            DataverseEndpoint = new Uri(request.EnvironmentInfo.DataverseUrl),
            EnvironmentId = request.EnvironmentInfo.EnvironmentId,
            AccountInfo = request.AccountInfo,
            SolutionVersions = request.SolutionVersions,
            AgentManagementEndpoint = new Uri(request.EnvironmentInfo.AgentManagementUrl)
        };

        public static async Task<ImmutableArray<ConnectionNeeded>> ProvisionAndGetConnectionsAsync(IWorkspaceSynchronizer synchronizer, DirectoryPath workspaceFolder, DefinitionBase definition, ISyncDataverseClient dataverseClient, CancellationToken cancellationToken)
        {
            var connectorPushResult = await synchronizer.PushCustomConnectorsAsync(workspaceFolder, dataverseClient, cancellationToken);
            await synchronizer.ProvisionConnectionReferencesAsync(workspaceFolder, definition, dataverseClient, cancellationToken, connectorPushResult.PushedRowIds);
            var agentConnections = await synchronizer.GetAgentConnectionReferencesAsync(workspaceFolder, definition, dataverseClient, cancellationToken);
            return agentConnections.ToImmutableArray();
        }

        public static async Task<ImmutableArray<ConnectionNeeded>> ProvisionAndGetNewConnectionsAsync(IWorkspaceSynchronizer synchronizer, DirectoryPath workspaceFolder, DefinitionBase definition, ISyncDataverseClient dataverseClient, CancellationToken cancellationToken)
        {
            var connectorPushResult = await synchronizer.PushCustomConnectorsAsync(workspaceFolder, dataverseClient, cancellationToken);
            await synchronizer.ProvisionConnectionReferencesAsync(workspaceFolder, definition, dataverseClient, cancellationToken, connectorPushResult.PushedRowIds);
            var agentConnections = await synchronizer.GetNewAgentConnectionReferencesAsync(workspaceFolder, definition, dataverseClient, cancellationToken);
            return agentConnections.ToImmutableArray();
        }

        public static async Task BindConnectionsAsync(ISyncDataverseClient dataverseClient, ImmutableArray<ConnectionBindingInput> bindings, ILspLogger logger, CancellationToken cancellationToken)
        {
            if (bindings.IsDefaultOrEmpty)
            {
                return;
            }

            foreach (var binding in bindings)
            {
                if (string.IsNullOrWhiteSpace(binding.ConnectionReferenceLogicalName) || string.IsNullOrWhiteSpace(binding.ConnectionLogicalName))
                {
                    continue;
                }

                try
                {
                    await dataverseClient.BindConnectionReferenceAsync(binding.ConnectionReferenceLogicalName, binding.ConnectionLogicalName, cancellationToken, binding.ConnectionDisplayName);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogSensitiveInformation($"Failed to bind connection reference '{binding.ConnectionReferenceLogicalName}': {ex.Message}", "Failed to bind a connection reference.");
                    throw new ConnectionBindingException("Failed to bind a connection reference.", ex);
                }
            }
        }
    }

    internal sealed class ConnectionBindingException : InvalidOperationException
    {
        public ConnectionBindingException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
