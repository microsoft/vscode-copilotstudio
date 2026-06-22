// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.CopilotStudio.Sync;

/// <summary>
/// Optional connection-management surface used by the Visual Studio Code connection manager.
/// This is intentionally separate from <see cref="IWorkspaceSynchronizer"/> so that hosts which
/// only need core sync (clone/pull/push) are not required to implement connection-management
/// behavior. Hosts opt in by resolving this service; <see cref="WorkspaceSynchronizer"/> implements it.
/// </summary>
public interface IConnectionManagementService
{
    /// <summary>
    /// Get the agent's connection references.
    /// </summary>
    /// <param name="workspaceFolder">Workspace folder.</param>
    /// <param name="definition">The bot definition.</param>
    /// <param name="dataverseClient">Dataverse client used to read binding state.</param>
    /// <param name="catalogClient">Client used to list existing cloud connections per connector.</param>
    /// <param name="catalogContext">Power Apps context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The declared connection references with binding state and selectable candidates.</returns>
    Task<IReadOnlyList<AgentConnectionView>> GetAgentConnectionViewsAsync(
        DirectoryPath workspaceFolder,
        DefinitionBase definition,
        ISyncDataverseClient dataverseClient,
        IConnectionCatalogClient catalogClient,
        PowerAppsContext catalogContext,
        CancellationToken cancellationToken);

    /// <summary>
    /// Binds connection references to existing cloud connections.
    /// </summary>
    /// <param name="workspaceFolder">Workspace folder.</param>
    /// <param name="definition">The bot definition.</param>
    /// <param name="dataverseClient">Dataverse client used to bind and read binding state.</param>
    /// <param name="catalogClient">Client used to list existing cloud connections per connector.</param>
    /// <param name="catalogContext">Power Apps context.</param>
    /// <param name="bindings">The connection reference to connection bindings to apply.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The refreshed connection views after binding.</returns>
    Task<IReadOnlyList<AgentConnectionView>> ApplyConnectionBindingsAsync(
        DirectoryPath workspaceFolder,
        DefinitionBase definition,
        ISyncDataverseClient dataverseClient,
        IConnectionCatalogClient catalogClient,
        PowerAppsContext catalogContext,
        IReadOnlyList<ConnectionBindingRequest> bindings,
        CancellationToken cancellationToken);

    /// <summary>
    /// Writes the connection views to the connections cache.
    /// </summary>
    /// <param name="workspaceFolder">Workspace folder.</param>
    /// <param name="views">The connection views to cache.</param>
    void WriteConnectionsCache(DirectoryPath workspaceFolder, IReadOnlyList<AgentConnectionView> views);

    /// <summary>
    /// Get the current generation of the workspace's connections cache. 
    /// </summary>
    /// <param name="workspaceFolder">The workspace folder.</param>
    /// <returns>The current cache generation.</returns>
    long GetConnectionsCacheGeneration(DirectoryPath workspaceFolder);

    /// <summary>
    /// Writes the connection views to the connections cache.
    /// </summary>
    /// <param name="workspaceFolder">The workspace folder.</param>
    /// <param name="views">The connection views to persist.</param>
    /// <param name="expectedGeneration">The generation captured before the read that produced views.</param>
    /// <returns>True when the cache was written; false when a newer write made the views stale.</returns>
    bool TryWriteConnectionsCache(DirectoryPath workspaceFolder, IReadOnlyList<AgentConnectionView> views, long expectedGeneration);

    /// <summary>
    /// Declares connection references and writes them into the declared connection reference files.
    /// </summary>
    /// <param name="workspaceFolder">Workspace folder.</param>
    /// <param name="definition">The bot definition.</param>
    /// <param name="logicalNames">The connection reference logical names to declare.</param>
    /// <param name="dataverseClient">Dataverse client used to create the connection reference records.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The declared and invalid logical names.</returns>
    Task<DeclareConnectionReferencesResult> DeclareConnectionReferencesAsync(
        DirectoryPath workspaceFolder,
        DefinitionBase definition,
        IReadOnlyList<string> logicalNames,
        ISyncDataverseClient dataverseClient,
        CancellationToken cancellationToken);

    /// <summary>
    /// Creates and declares a new connection reference for the given connector.
    /// </summary>
    /// <param name="workspaceFolder">Workspace folder.</param>
    /// <param name="definition">The bot definition.</param>
    /// <param name="connectorInternalId">The connector internal id to create the reference for (for example <c>shared_office365</c>).</param>
    /// <param name="dataverseClient">Dataverse client used to create the connection reference record.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The new connection reference logical name.</returns>
    Task<string> CreateConnectionReferenceForConnectorAsync(
        DirectoryPath workspaceFolder,
        DefinitionBase definition,
        string connectorInternalId,
        ISyncDataverseClient dataverseClient,
        CancellationToken cancellationToken);

    /// <summary>
    /// Removes a connection reference declaration from the workspace's local connection reference files.
    /// </summary>
    /// <param name="workspaceFolder">Workspace folder.</param>
    /// <param name="definition">The bot definition.</param>
    /// <param name="logicalName">The connection reference logical name to remove.</param>
    /// <param name="confirmed">True to remove even when the reference is still used by a component.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The removal result; when blocked by usages and not confirmed, the blocking usages.</returns>
    Task<ConnectionReferenceRemovalResult> RemoveConnectionReferenceAsync(
        DirectoryPath workspaceFolder,
        DefinitionBase definition,
        string logicalName,
        bool confirmed,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads the on-disk connections cache, or returns null when it is missing or unreadable.
    /// </summary>
    /// <param name="workspaceFolder">Workspace folder that owns the cache.</param>
    /// <returns>The cached connection views, or null.</returns>
    ConnectionsCacheFile? ReadConnectionsCache(DirectoryPath workspaceFolder);
}
