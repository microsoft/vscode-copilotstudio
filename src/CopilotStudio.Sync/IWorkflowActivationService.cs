// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.CopilotStudio.Sync;

/// <summary>
/// Optional workflow activation surface used by the Visual Studio Code connection manager to
/// enable or disable an agent's cloud flows. This is intentionally separate from
/// <see cref="IWorkspaceSynchronizer"/> so that hosts which only need core sync are not required
/// to implement it. <see cref="WorkspaceSynchronizer"/> implements it.
/// </summary>
public interface IWorkflowActivationService
{
    /// <summary>
    /// Get the agent's workflow activation status.
    /// </summary>
    /// <param name="workspaceFolder">Workspace folder that owns the workflows.</param>
    /// <param name="views">The current connection views used to determine whether each workflow can be enabled.</param>
    /// <returns>The workflow status views.</returns>
    IReadOnlyList<WorkflowStatusView> GetWorkflowStatusViews(DirectoryPath workspaceFolder, IReadOnlyList<AgentConnectionView> views);

    /// <summary>
    /// Activates or deactivates one or more workflows in a single pass.
    /// </summary>
    /// <param name="workspaceFolder">Workspace folder that owns the workflows.</param>
    /// <param name="requests">The per-workflow activation requests to apply.</param>
    /// <param name="dataverseClient">Dataverse client used to change the workflow states.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result of the batch with the refreshed workflow status views.</returns>
    Task<WorkflowActivationResult> SetWorkflowActivationsAsync(DirectoryPath workspaceFolder, IReadOnlyList<WorkflowActivationRequest> requests, ISyncDataverseClient dataverseClient, CancellationToken cancellationToken);
}
