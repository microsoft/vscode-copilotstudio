namespace Microsoft.CopilotStudio.Sync.Dataverse;

public interface ISyncComponentCollectionDataverseClient
{
    /// <summary>
    /// Create a new component collection by name and schema name.
    /// </summary>
    Task<ComponentCollectionInfo> CreateComponentCollectionAsync(string displayName, string schemaName, CancellationToken cancellationToken);

    /// <summary>
    /// Get a component collection with the given schemaName.
    /// </summary>
    Task<Guid> GetComponentCollectionIdBySchemaNameAsync(string schemaName, CancellationToken cancellationToken);

    /// <summary>
    /// Install an existing component collection on an agent.
    /// </summary>
    Task InstallComponentCollectionOnAgentAsync(Guid agentId, Guid componentCollectionId, CancellationToken cancellationToken);

    /// <summary>
    /// Remove an installed component collection from an agent.
    /// </summary>
    Task UninstallComponentCollectionFromAgentAsync(Guid agentId, Guid componentCollectionId, CancellationToken cancellationToken);

    /// <summary>
    /// Get the ids of agents (bots) that have the given component collection installed.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetAgentIdsForComponentCollectionAsync(Guid componentCollectionId, CancellationToken cancellationToken);
}
