// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.CopilotStudio.Sync.Hosting;

internal static class WorkflowActivationStage
{
    public static async Task<WorkflowActivationPassResult> ActivateAsync(AgentWorkspaceCall call, DirectoryPath workspaceFolder, DefinitionBase definition, CancellationToken cancellationToken)
    {
        call.Enter(AgentPushStages.Activation);
        var pass = await call.ActivationPass.RunAsync(workspaceFolder, definition, call.Dataverse, WorkflowActivationPass.NoConnectionCatalog.Instance, WorkflowActivationPass.NoConnectionCatalog.Context, null, cancellationToken).ConfigureAwait(false);

        return pass.Failure is { } failure ? throw new AgentWorkspaceHostException(AgentWorkspaceHostErrorCodes.PushFailed, $"The content was pushed, but the workflows could not be activated: {DataverseFailureText.Of(failure)}", new[] { AgentPushStages.Failure(call.Stage) }, failure) : pass;
    }

    public static IReadOnlyList<DraftFlow> FlowsLeftInDraft(WorkflowActivationPassResult pass)
    {
        var unbound = new HashSet<string>(pass.UnresolvedConnections.Select(decision => decision.View.ConnectionReferenceLogicalName), StringComparer.OrdinalIgnoreCase);

        return pass.Workflows.Where(workflow => !IsRunning(workflow)).Select(workflow => new DraftFlow(workflow.WorkflowId, workflow.DisplayName, workflow.ConnectionReferenceLogicalNames.Where(unbound.Contains).ToArray())).ToArray();
    }

    public static bool IsRunning(WorkflowActivationPassItem workflow) => workflow.IsObserved && workflow.State == WorkflowState.Activated;

    public static IEnumerable<AgentSyncDiagnostic> Describe(WorkflowActivationPassResult pass) => pass.RefusalMessage is { } refusal ? new[] { new AgentSyncDiagnostic(AgentSyncDiagnosticCodes.ActivationRefused, refusal) { Subject = pass.RefusedWorkflows.Length == 1 ? pass.RefusedWorkflows[0] : null } } : Array.Empty<AgentSyncDiagnostic>();
}
