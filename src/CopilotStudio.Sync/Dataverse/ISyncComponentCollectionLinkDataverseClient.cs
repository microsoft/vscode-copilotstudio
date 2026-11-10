// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.CopilotStudio.Sync.Dataverse;

/// <summary>Reads component collections and the agents they are linked to, many collections per request.</summary>
public interface ISyncComponentCollectionLinkDataverseClient
{
    /// <summary>Gets the agents each named component collection is linked to, keyed by collection schema name, omitting the collections the environment does not hold.</summary>
    /// <param name="schemaNames">The schema names of the collections to read.</param>
    /// <param name="cancellationToken">Cancels the requests.</param>
    Task<IReadOnlyDictionary<string, IReadOnlyList<AgentInfo>>> GetLinkedAgentsByComponentCollectionSchemaNamesAsync(IEnumerable<string> schemaNames, CancellationToken cancellationToken);

    /// <summary>Reads the component collections with the given schema names, keyed by schema name and omitting the ones the environment does not hold.</summary>
    /// <param name="schemaNames">The schema names of the collections to read.</param>
    /// <param name="cancellationToken">Cancels the requests.</param>
    Task<IReadOnlyDictionary<string, ComponentCollectionInfo>> GetComponentCollectionsBySchemaNamesAsync(IEnumerable<string> schemaNames, CancellationToken cancellationToken);
}
