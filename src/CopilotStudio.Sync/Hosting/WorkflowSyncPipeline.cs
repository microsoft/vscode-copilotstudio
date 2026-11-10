// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.CopilotStudio.McsCore;

namespace Microsoft.CopilotStudio.Sync.Hosting;

internal sealed class WorkflowSyncPipeline
{
    private readonly AgentWorkspaceCall _call;

    public WorkflowSyncPipeline(AgentWorkspaceCall call) => _call = call;

    public async Task<WorkflowCloneResult> CloneAsync(WorkflowCloneRequest request, Guid workflowId, CancellationToken cancellationToken)
    {
        var workspace = _call.Open(request.Folder);
        await _call.Workflows.DownloadAsync(workflowId, workspace, request.Source.EnvironmentId, cancellationToken).ConfigureAwait(false);

        return new WorkflowCloneResult(WorkspaceSnapshotFiles.Read(request.Folder, new[] { new KeyValuePair<string, IFileAccessor>(request.Folder, workspace) }), workflowId.ToString());
    }

    public async Task<WorkflowPushResult> PushAsync(AgentWorkspaceSnapshot snapshot, WorkflowSyncTarget target, WorkflowFile workflow, AgentPushOptions options, CancellationToken cancellationToken)
    {
        _call.EnterFirstStage(AgentPushStages.Workflows, options.StageStarted);
        WorkspaceSnapshotFiles.Write(_call.Files, _call.Root, snapshot);
        var folder = _call.FolderOf(snapshot.Folder);
        var workspace = _call.Files.Create(folder);
        WorkflowDefinitionFile.WriteConnectionReferenceDeclaration(workspace);
        cancellationToken.ThrowIfCancellationRequested();

        var upsert = await _call.Workflows.UpsertAsync(workspace, target.EnvironmentId, cancellationToken, new StandaloneWorkflowUpsertOptions { OverwriteUnpublishedDraft = true }).ConfigureAwait(false);
        var diagnostics = new List<AgentSyncDiagnostic>();

        if (upsert.OverwroteUnpublishedDraft)
        {
            diagnostics.Add(new AgentSyncDiagnostic(AgentSyncDiagnosticCodes.UnpublishedDraftOverwritten, $"The workflow '{upsert.Workflow.Name}' ({workflow.WorkflowId}) held a draft saved in the designer but not published in {target.EnvironmentId}; the push overwrote it.", snapshot.Folder + WorkspaceSnapshotFiles.Separator + workflow.MetadataPath) { Subject = upsert.Workflow.Name });
        }

        if (!string.IsNullOrEmpty(upsert.Warning) && !upsert.DraftedForConnections)
        {
            diagnostics.Add(new AgentSyncDiagnostic(AgentSyncDiagnosticCodes.ActivationRefused, upsert.Warning) { Subject = upsert.Workflow.Name });
        }

        cancellationToken.ThrowIfCancellationRequested();
        var activation = await WorkflowActivationStage.ActivateAsync(_call, folder, new BotDefinition(), cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(WorkflowActivationStage.Describe(activation));
        var observed = activation.Workflows.FirstOrDefault(item => item.IsObserved && Guid.TryParse(item.WorkflowId, out var observedId) && observedId == workflow.WorkflowId);
        var activated = observed is null ? upsert.Workflow.IsActivated : WorkflowActivationStage.IsRunning(observed);

        return new WorkflowPushResult(activated, activated ? Array.Empty<string>() : upsert.UnboundConnectionReferences, diagnostics);
    }
}
