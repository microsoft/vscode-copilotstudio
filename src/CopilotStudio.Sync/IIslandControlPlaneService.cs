// Copyright (C) Microsoft Corporation. All rights reserved.
// Ported from om/src/vscode/LanguageServers/PowerPlatformLS/Contracts.FileLayout/IIslandControlPlaneService.cs

using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.Platform.Content;
using System.Threading;

namespace Microsoft.CopilotStudio.Sync;

public interface IIslandControlPlaneService
{
    void SetIslandBaseEndpoint(string baseEndpoint);

    /// <summary>
    /// Sets the connection context for authenticated requests.
    /// Must be called before GetComponentsAsync or SaveChangesAsync.
    /// Auth is provided via <see cref="ISyncAuthProvider"/> (constructor-injected).
    /// </summary>
    void SetConnectionContext(string baseEndpoint, CoreServicesClusterCategory clusterCategory);

    Task<PvaComponentChangeSet> GetComponentsAsync(AuthoringOperationContextBase operationContext, string? changeToken, CancellationToken cancellationToken);

    Task<PvaComponentChangeSet> SaveChangesAsync(AuthoringOperationContextBase operationContext, PvaComponentChangeSet pushChangeset, CancellationToken cancellationToken);
}
