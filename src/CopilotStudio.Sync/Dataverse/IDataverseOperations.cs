// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Threading;
using static Microsoft.CopilotStudio.Sync.Dataverse.SyncDataverseClient;

namespace Microsoft.CopilotStudio.Sync.Dataverse;

/// <summary>Points a Dataverse client at one environment.</summary>
public interface IDataverseEnvironmentTarget
{
    /// <summary>Sets the environment every later call targets.</summary>
    /// <param name="dataverseUrl">The environment's Dataverse endpoint.</param>
#pragma warning disable CA1054 // URI parameter is used as string prefix for request URL construction
    void SetDataverseUrl(string dataverseUrl);
#pragma warning restore CA1054
}

/// <summary>Reads and writes the connection references an environment holds.</summary>
public interface IConnectionReferenceOperations : IDataverseEnvironmentTarget
{
    /// <summary>Creates a connection reference when the environment does not already hold one.</summary>
    /// <param name="connectionReferenceLogicalName">The connection reference to create.</param>
    /// <param name="connectorId">The connector the reference targets.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <param name="customConnectorRowId">The custom connector row the reference targets, when it is not a first-party connector.</param>
    Task EnsureConnectionReferenceExistsAsync(string connectionReferenceLogicalName, string connectorId, CancellationToken cancellationToken, Guid? customConnectorRowId = null);

    /// <summary>Reads connection references by logical name, skipping any the environment does not hold.</summary>
    /// <param name="logicalNames">The connection references to read.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The references the environment holds, with the connection each is bound to.</returns>
    Task<ConnectionReferenceInfo[]> GetConnectionReferencesByLogicalNamesAsync(IEnumerable<string> logicalNames, CancellationToken cancellationToken);
}

/// <summary>Starts and stops the workflows an environment holds.</summary>
public interface IWorkflowStateOperations
{
    /// <summary>Starts or stops a workflow.</summary>
    /// <param name="workflowId">The workflow whose state changes.</param>
    /// <param name="activate">Whether the workflow should run.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task SetWorkflowStateAsync(Guid workflowId, bool activate, CancellationToken cancellationToken);
}

/// <summary>Reads and saves the custom connectors an environment holds.</summary>
public interface ICustomConnectorOperations
{
    /// <summary>Gets the versions of connectors by their connector ids.</summary>
    /// <param name="connectorIds">Connector ids to look up, ignoring duplicates and empty values.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task<CustomConnectorMetadata[]> GetConnectorVersionsByIdsAsync(IReadOnlyCollection<Guid> connectorIds, CancellationToken cancellationToken);

    /// <summary>Creates or updates a custom connector and returns the copy the environment stored.</summary>
    /// <param name="connector">The custom connector metadata to save.</param>
    /// <param name="exists">True to update the existing connector; false to create it.</param>
    /// <param name="expectedVersion">When set, an update applies only while the stored connector still has this version, in environments that check versions.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task<CustomConnectorMetadata> SaveConnectorAsync(CustomConnectorMetadata connector, bool exists, long? expectedVersion, CancellationToken cancellationToken);
}
