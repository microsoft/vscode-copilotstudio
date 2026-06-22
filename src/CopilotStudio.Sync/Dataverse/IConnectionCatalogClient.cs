// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Threading;

namespace Microsoft.CopilotStudio.Sync.Dataverse;

/// <summary>
/// Lists existing cloud connections for a connector.
/// </summary>
public interface IConnectionCatalogClient
{
    /// <summary>
    /// Returns the existing connections of the given connector type in the context's environment.
    /// </summary>
    /// <param name="context">Power Apps context.</param>
    /// <param name="connectorName">The connector internal id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<ConnectionInstance>> ListConnectionsAsync(PowerAppsContext context, string connectorName, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the connectors available in the context's environment.
    /// </summary>
    /// <param name="context">Power Apps context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<ConnectorInfo>> ListConnectorsAsync(PowerAppsContext context, CancellationToken cancellationToken);
}