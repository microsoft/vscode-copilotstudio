// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.Platform.Content;
using System.Threading;

namespace Microsoft.CopilotStudio.Sync;

public interface IIslandControlPlaneService
{
    Task<PvaComponentChangeSet> GetComponentsAsync(AuthoringOperationContextBase operationContext, string? changeToken, CancellationToken cancellationToken);

    Task<PvaComponentChangeSet> SaveChangesAsync(AuthoringOperationContextBase operationContext, PvaComponentChangeSet pushChangeset, CancellationToken cancellationToken);
}
