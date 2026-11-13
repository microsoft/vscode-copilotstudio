// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.CopilotStudio.Sync.Copy;

/// <summary>The Dataverse operations a copy needs beyond <see cref="ISyncDataverseClient"/>.</summary>
public interface ICopyDataverseClient
{
    /// <summary>Resolves an agent by schema name.</summary>
    /// <param name="schemaName">The schema name.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The agent identifier, or <see cref="Guid.Empty"/> when no agent carries the name.</returns>
    Task<Guid> GetAgentIdBySchemaNameAsync(string schemaName, CancellationToken cancellationToken);

    /// <summary>Finds components whose schema name starts with a prefix, in any agent.</summary>
    /// <param name="schemaNamePrefix">The prefix.</param>
    /// <param name="maximumCount">The most names to return.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Matching schema names.</returns>
    Task<IReadOnlyList<string>> FindComponentSchemaNamesAsync(string schemaNamePrefix, int maximumCount, CancellationToken cancellationToken);

    /// <summary>The workflows among the identifiers that exist.</summary>
    /// <param name="workflowIds">The identifiers to check.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The identifiers that exist.</returns>
    Task<IReadOnlyCollection<Guid>> FindExistingWorkflowIdsAsync(IEnumerable<Guid> workflowIds, CancellationToken cancellationToken);

    /// <summary>The AI models among the identifiers that exist.</summary>
    /// <param name="modelIds">The identifiers to check.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The identifiers that exist.</returns>
    Task<IReadOnlyCollection<Guid>> FindExistingAIModelIdsAsync(IEnumerable<Guid> modelIds, CancellationToken cancellationToken);

    /// <summary>The custom connectors among the identifiers that exist.</summary>
    /// <param name="connectorIds">The identifiers to check.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The identifiers that exist.</returns>
    Task<IReadOnlyCollection<Guid>> FindExistingConnectorIdsAsync(IEnumerable<Guid> connectorIds, CancellationToken cancellationToken);

    /// <summary>Reads connection references by logical name.</summary>
    /// <param name="logicalNames">The names to read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The rows that exist.</returns>
    Task<SyncDataverseClient.ConnectionReferenceInfo[]> GetConnectionReferencesByLogicalNamesAsync(IEnumerable<string> logicalNames, CancellationToken cancellationToken);

    /// <summary>Reads the suffix the environment ends the internal identifiers of the custom connectors it creates with.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The suffix, or <see langword="null"/> when no unmanaged custom connector shows it or the connectors disagree.</returns>
    Task<string?> FindConnectorEnvironmentSuffixAsync(CancellationToken cancellationToken);

    /// <summary>Finds the custom connectors whose internal identifier starts with any of the prefixes.</summary>
    /// <param name="prefixes">The internal identifier prefixes.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The matching connectors.</returns>
    Task<CustomConnectorMetadata[]> FindConnectorsByInternalIdPrefixesAsync(IEnumerable<string> prefixes, CancellationToken cancellationToken);

    /// <summary>Finds the unmanaged cloud flows that carry a name, compared ignoring case.</summary>
    /// <param name="name">The name to look for.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The flows with the name, at most one.</returns>
    Task<IReadOnlyList<SyncDataverseClient.WorkflowSummary>> FindCloudFlowsNamedAsync(string name, CancellationToken cancellationToken);
}
