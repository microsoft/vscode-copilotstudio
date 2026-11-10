// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Collections.Immutable;
using Microsoft.Agents.ObjectModel;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using static Microsoft.CopilotStudio.Sync.Dataverse.SyncDataverseClient;

namespace Microsoft.CopilotStudio.Sync.Hosting;

internal sealed class AgentPushPipeline
{
    private readonly AgentWorkspaceCall _call;

    public AgentPushPipeline(AgentWorkspaceCall call) => _call = call;

    public async Task<AgentPushResult> RunAsync(AgentWorkspaceSnapshot snapshot, AgentSyncTarget target, Guid? botId, AgentPushOptions options, CancellationToken cancellationToken)
    {
        _call.EnterFirstStage(AgentPushStages.Agent, options.StageStarted);
        WorkspaceSnapshotFiles.Write(_call.Files, _call.Root, snapshot);
        var agentFolder = _call.FolderOf(snapshot.Folder);
        cancellationToken.ThrowIfCancellationRequested();

        var attached = await _call.AttachService.AttachAsync(new WorkspaceAttachRequest { WorkspaceFolder = agentFolder, DataverseEndpoint = target.DataverseEndpoint, EnvironmentId = target.EnvironmentId, TenantId = target.TenantId, DisplayName = target.DisplayName, SchemaName = target.SchemaName, ExpectedAgentId = botId, CreateIfMissing = options.CreateIfMissing, PushAuxiliaryContent = false, ProvisioningTimeout = options.ProvisioningTimeout }, _call.Dataverse, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        _call.Enter(AgentPushStages.Components);
        var collections = ComponentCollectionFolders.Resolve(_call.Files, _call.Root, agentFolder);
        var declared = collections.Found.ToDictionary(folder => folder, folder => WorkspaceSynchronizer.ReadComponentCollectionOrNull(_call.Files.Create(folder)) is { } collection && !string.IsNullOrWhiteSpace(collection.SchemaName.Value) ? collection : throw UnreadableCollection(folder, snapshot.Folder));
        var existing = declared.Count == 0 ? ImmutableDictionary<string, ComponentCollectionInfo>.Empty : await _call.CollectionLinks.GetComponentCollectionsBySchemaNamesAsync(declared.Values.Select(collection => collection.SchemaName.Value), cancellationToken).ConfigureAwait(false);
        var agentPush = new WorkspacePushRequest { WorkspaceFolder = agentFolder, Overwrite = true, DeclaredCollections = declared.Values.ToArray(), DiscardRefusedWorkflows = true, AgentProvisioned = true, StageStarted = _call.Enter };

        var pushed = await ComponentCollectionPushSequence.PushAsync(collections, agentFolder, (folder, token) => folder == agentFolder ? PushAsync(agentPush, token) : PushCollectionAsync(folder, declared[folder], existing, attached.SyncInfo, token), null, cancellationToken).ConfigureAwait(false);
        var diagnostics = pushed.Collections.Select(collection => collection.Pushed).Append(pushed.Agent).SelectMany(Describe).ToList();
        cancellationToken.ThrowIfCancellationRequested();

        var activation = await WorkflowActivationStage.ActivateAsync(_call, agentFolder, pushed.Agent.Definition, cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(WorkflowActivationStage.Describe(activation));
        cancellationToken.ThrowIfCancellationRequested();

        _call.Enter(AgentPushStages.Components);
        var shared = await ComponentCollectionFolders.GetOtherLinkedAgentsAsync(_call.Files, pushed.Collections.Select(collection => collection.Folder).ToArray(), attached.AgentId, _call.Dataverse, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var published = options.Publish ? await PublishAsync(attached.AgentId, options.PublishTimeout, diagnostics, cancellationToken).ConfigureAwait(false) : null;

        return new AgentPushResult(attached.AgentId.ToString(), published?.Kind == AgentPublishOutcomeKind.Succeeded, WorkflowActivationStage.FlowsLeftInDraft(activation), shared.Where(links => !links.OtherAgents.IsEmpty).Select(links => new SharedCollection(links.SchemaName, links.OtherAgents.Select(agent => agent.SchemaName is null || string.IsNullOrWhiteSpace(agent.SchemaName) ? agent.AgentId.ToString() : agent.SchemaName).ToArray())).ToArray(), diagnostics) { PublishedOn = published is { Kind: AgentPublishOutcomeKind.Succeeded } succeeded ? succeeded.FinishedOn : pushed.Agent.PublishedOn, ChangeProbe = options.ReadChangeProbe ? await _call.ProbeOrNullAsync(attached.AgentId, target.SchemaName, cancellationToken).ConfigureAwait(false) : null };
    }

    private static IEnumerable<AgentSyncDiagnostic> Describe(WorkspacePushResult pushed) => pushed.Diagnostics.Where(diagnostic => diagnostic.Code != WorkspacePushDiagnosticCodes.WorkflowNotRunning).Select(diagnostic => new AgentSyncDiagnostic(diagnostic.Code, diagnostic.Message) { Subject = diagnostic.Subject, Detail = diagnostic.Detail });

    private static AgentWorkspaceHostException UnreadableCollection(DirectoryPath folder, string agentFolder) => new(AgentWorkspaceHostErrorCodes.SnapshotInvalid, $"A component collection declared by '{agentFolder}' names no schema name.", new[] { new AgentSyncDiagnostic(AgentSyncDiagnosticCodes.CollectionProjectMissing, $"The collection project in '{folder}' names no schema name.") });

    private Task<WorkspacePushResult> PushAsync(WorkspacePushRequest request, CancellationToken cancellationToken) => _call.PushService.PushAsync(request, _call.Dataverse, cancellationToken);

    private async Task<WorkspacePushResult> PushCollectionAsync(DirectoryPath folder, BotComponentCollection collection, IReadOnlyDictionary<string, ComponentCollectionInfo> existing, AgentSyncInfo agentSyncInfo, CancellationToken cancellationToken)
    {
        var collectionId = await WorkspaceSynchronizer.ResolveComponentCollectionIdAsync(_call.Collections, collection, existing.TryGetValue(collection.SchemaName.Value, out var found) ? found.Id : Guid.Empty, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var syncInfo = WorkspaceCloneSequence.ForComponentCollection(agentSyncInfo, collectionId);

        await _call.RetargetService.SyncWorkspaceAsync(folder, await _call.OperationContexts.GetAsync(syncInfo).ConfigureAwait(false), null, false, _call.Dataverse, syncInfo, null, cancellationToken, ImmutableArray<AIPromptMetadata>.Empty, syncCustomConnectors: false, syncWorkflowsAndPrompts: false).ConfigureAwait(false);
        await _call.Synchronizer.SaveSyncInfoAsync(folder, syncInfo).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        return await PushAsync(new WorkspacePushRequest { WorkspaceFolder = folder, Overwrite = true, DiscardRefusedWorkflows = true, StageStarted = _call.Enter }, cancellationToken).ConfigureAwait(false);
    }

    private static AgentSyncDiagnostic? DiagnosticOf(AgentPublishOutcome outcome, TimeSpan timeout) => outcome.Kind switch
    {
        AgentPublishOutcomeKind.Succeeded => null,
        AgentPublishOutcomeKind.TimedOut => new AgentSyncDiagnostic(AgentSyncDiagnosticCodes.PublishTimedOut, outcome.FailureReason is { } reason ? $"The bot was not published: {reason} Deploy again once Copilot Studio finishes provisioning it." : $"Copilot Studio did not finish publishing the bot within {timeout}; the publish may still complete. Deploy again to confirm."),
        _ => new AgentSyncDiagnostic(AgentSyncDiagnosticCodes.PublishFailed, outcome.Status is { } status ? $"Copilot Studio finished publishing the bot with status '{status}'." : $"Copilot Studio was asked to publish the bot, but its progress could not be read. {outcome.FailureReason}"),
    };

    private async Task<AgentPublishOutcome?> PublishAsync(Guid botId, TimeSpan timeout, List<AgentSyncDiagnostic> diagnostics, CancellationToken cancellationToken)
    {
        _call.Enter(AgentPushStages.Publish);
        AgentPublishOutcome outcome;

        try
        {
            outcome = await _call.PublishService.PublishAsync(botId, timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OperationCanceledException and not AgentWorkspaceHostException)
        {
            diagnostics.Add(new AgentSyncDiagnostic(AgentSyncDiagnosticCodes.PublishFailed, $"The bot was pushed but could not be published: {DataverseFailureText.Of(failure)}"));

            return null;
        }

        if (DiagnosticOf(outcome, timeout) is { } diagnostic)
        {
            diagnostics.Add(diagnostic);
        }

        return outcome;
    }
}