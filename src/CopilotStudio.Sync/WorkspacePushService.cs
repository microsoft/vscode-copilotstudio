// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Collections.Immutable;
using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.Platform.Content;
using Microsoft.Agents.Platform.Content.Exceptions;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using static Microsoft.CopilotStudio.Sync.Dataverse.SyncDataverseClient;

namespace Microsoft.CopilotStudio.Sync;

/// <summary>Pushes a connected agent or component-collection workspace in the order the service needs, with the retries and typed failures every host shares.</summary>
public interface IWorkspacePushService
{
    /// <summary>Waits until Copilot Studio no longer reports the agent's bot as provisioning, unless the bot was created from a CLI agent template and so is provisioned only by its first publish, then pushes the custom connectors, connection references, workflows, AI prompts, components and knowledge files.</summary>
    /// <param name="request">The workspace to push and how.</param>
    /// <param name="dataverseClient">The client, already targeting the environment the workspace is connected to.</param>
    /// <param name="cancellationToken">Cancels the push.</param>
    Task<WorkspacePushResult> PushAsync(WorkspacePushRequest request, ISyncDataverseClient dataverseClient, CancellationToken cancellationToken);
}

/// <summary>Pushes connected workspaces, re-reading the cloud copy and sending the components again when the service allows another attempt.</summary>
public sealed class WorkspacePushService : IWorkspacePushService
{
    internal const int UnreadableReplyAttempts = 4;

    private const string UnreadableReplyMarker = "Improper response, not implemented";

    private const string MissingEntityMarker = "does not exist";

    private const string DuplicateConnectionReferenceMarker = "ExportKeyInvalidCreate";

    private const string ConnectionReferenceEntity = "connectionreference";

    private const string ObjectDoesNotExistCode = "0x80040217";

    private readonly IWorkspaceSynchronizer _synchronizer;

    private readonly IWorkspaceRetargetService _retargetService;

    private readonly IOperationContextProvider _operationContexts;

    private readonly IFileAccessorFactory _fileAccessorFactory;

    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the service.</summary>
    /// <param name="synchronizer">Reads the workspace and writes its content to the service.</param>
    /// <param name="retargetService">Re-reads the cloud copy before the components are sent again.</param>
    /// <param name="operationContexts">Names the authoring context the components are pushed under.</param>
    /// <param name="fileAccessorFactory">Opens the workspace.</param>
    /// <param name="timeProvider">The clock that times the waits between attempts.</param>
    public WorkspacePushService(IWorkspaceSynchronizer synchronizer, IWorkspaceRetargetService retargetService, IOperationContextProvider operationContexts, IFileAccessorFactory fileAccessorFactory, TimeProvider timeProvider)
    {
        _synchronizer = synchronizer ?? throw new ArgumentNullException(nameof(synchronizer));
        _retargetService = retargetService ?? throw new ArgumentNullException(nameof(retargetService));
        _operationContexts = operationContexts ?? throw new ArgumentNullException(nameof(operationContexts));
        _fileAccessorFactory = fileAccessorFactory ?? throw new ArgumentNullException(nameof(fileAccessorFactory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <inheritdoc />
    public async Task<WorkspacePushResult> PushAsync(WorkspacePushRequest request, ISyncDataverseClient dataverseClient, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (dataverseClient is null)
        {
            throw new ArgumentNullException(nameof(dataverseClient));
        }

        AgentProvisioning.RequirePositive(request.ProvisioningTimeout, nameof(request));
        var folder = request.WorkspaceFolder;
        request.StageStarted?.Invoke(AgentPushStages.Components);
        var syncInfo = await _synchronizer.GetSyncInfoAsync(folder).ConfigureAwait(false);
        var operationContext = await _operationContexts.GetAsync(syncInfo).ConfigureAwait(false);
        var definition = await _synchronizer.ReadWorkspaceDefinitionAsync(folder, cancellationToken, checkKnowledgeFiles: true).ConfigureAwait(false);
        await _synchronizer.ThrowIfWorkspaceInvalidAsync(folder, definition, cancellationToken).ConfigureAwait(false);
        RequireWorkflowToolIds(definition);
        var (_, changes) = await _synchronizer.GetLocalChangesAsync(folder, definition, dataverseClient, syncInfo, cancellationToken).ConfigureAwait(false);

        if (!request.AgentProvisioned && syncInfo.AgentId is { } agentId)
        {
            await AgentProvisioning.RequireProvisionedAsync(dataverseClient as IAgentProvisioningDataverseClient, agentId, request.ProvisioningTimeout, _timeProvider, cancellationToken).ConfigureAwait(false);
        }

        var workspace = _fileAccessorFactory.Create(folder);
        var auxiliary = await AuxiliaryContent.PushAsync(_synchronizer, workspace, folder, dataverseClient, syncInfo.AgentId, request.Overwrite, WorkflowActivationMode.DraftWhenConnectionsUnbound, request.StageStarted, cancellationToken).ConfigureAwait(false);
        var refusals = Refusals(auxiliary.Connectors, auxiliary.WorkflowResponses, auxiliary.PromptResponses);

        if (request.DiscardRefusedWorkflows)
        {
            DiscardRefusedWorkflows(workspace, auxiliary.WorkflowResponses);
        }

        request.StageStarted?.Invoke(AgentPushStages.Components);
        var pushedDefinition = WithDeclaredCollections(definition, request.DeclaredCollections);
        await PushComponentsAsync(new ComponentPush(folder, operationContext, pushedDefinition, dataverseClient, syncInfo, auxiliary.CloudFlowMetadata, auxiliary.Prompts, request.Overwrite, refusals), cancellationToken).ConfigureAwait(false);
        var acceptedChanges = changes.WithoutFailedAssets(auxiliary.Connectors, auxiliary.WorkflowResponses, auxiliary.PromptResponses);
        return new WorkspacePushResult
        {
            Changes = acceptedChanges,
            AuxiliaryChangeCount = UnreportedWorkflowCount(acceptedChanges, auxiliary.WorkflowResponses),
            UploadedFileNames = await UploadKnowledgeFilesAsync(folder, dataverseClient, changes.Length, cancellationToken).ConfigureAwait(false),
            Definition = pushedDefinition,
            PublishedOn = (definition as BotDefinition)?.Entity?.PublishedOn is { } publishedOn ? new DateTimeOffset(publishedOn.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(publishedOn, DateTimeKind.Utc) : publishedOn.ToUniversalTime()) : null,
            Diagnostics = refusals,
        };
    }

    private static void RequireWorkflowToolIds(DefinitionBase definition)
    {
        var tools = (definition.Components.IsDefault ? ImmutableArray<BotComponentBase>.Empty : definition.Components).Where(HasEmptyWorkflowId).Select(NameOf).Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal).ToArray();

        if (tools.Length > 0)
        {
            throw new WorkspacePushException(WorkspacePushErrorCodes.WorkflowToolIdEmpty, $"These workflow tools name no workflow id: {string.Join(", ", tools)}. Pull the workflows they call, or remove the tools, before pushing.", null) { WorkflowTools = tools };
        }
    }

    private static bool HasEmptyWorkflowId(BotComponentBase component) => component is not null && component.DescendantsAndSelf().OfType<WorkflowTool>().Any(tool => !tool.WorkflowId.HasValue || tool.WorkflowId.Value == Guid.Empty);

    private static string NameOf(BotComponentBase component) => string.IsNullOrWhiteSpace(component.SchemaNameString) ? NameOf(component.DisplayName) : string.IsNullOrWhiteSpace(component.DisplayName) ? component.SchemaNameString : $"{component.DisplayName} ({component.SchemaNameString})";

    private static string NameOf(string? name) => name is null || string.IsNullOrWhiteSpace(name) ? "(unnamed)" : name;

    private static int UnreportedWorkflowCount(ImmutableArray<Change> reportedChanges, ImmutableArray<WorkflowResponse> workflowResponses)
    {
        var reportedAssetSchemaNames = reportedChanges.Select(change => SyncedAssetSchemaName.GetAssetSchemaName(change.SchemaName)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return workflowResponses.Count(response => !reportedAssetSchemaNames.Contains(WorkflowLayout.SchemaNamePrefix + response.WorkflowId));
    }

    private static IReadOnlyList<WorkspacePushDiagnostic> Refusals(CustomConnectorPushResult connectors, ImmutableArray<WorkflowResponse> workflowResponses, ImmutableArray<AIPromptResponse> promptResponses) => connectors.Failed.Zip(connectors.FailureReasons, (connector, reason) => new WorkspacePushDiagnostic(WorkspacePushDiagnosticCodes.ConnectorRefused, NameOf(connector.Name), null, DataverseFailureText.Redact(reason))).Concat(workflowResponses.Where(response => !string.IsNullOrEmpty(response.ErrorMessage)).Select(response => Refusal(WorkspacePushDiagnosticCodes.WorkflowRefused, NameOf(response.WorkflowName), response.ErrorMessage, "workflow"))).Concat(workflowResponses.Where(response => string.IsNullOrEmpty(response.ErrorMessage) && response.IsDisabled).Select(response => new WorkspacePushDiagnostic(WorkspacePushDiagnosticCodes.WorkflowNotRunning, NameOf(response.WorkflowName), null, $"The environment left the workflow '{NameOf(response.WorkflowName)}' disabled."))).Concat(promptResponses.Where(response => !string.IsNullOrEmpty(response.ErrorMessage)).Select(response => Refusal(WorkspacePushDiagnosticCodes.PromptRefused, NameOf(response.PromptName), response.ErrorMessage, "AI prompt"))).ToArray();

    private static WorkspacePushDiagnostic Refusal(string code, string name, string? errorMessage, string kind)
    {
        var detail = DataverseFailureText.Redact(errorMessage);

        return new WorkspacePushDiagnostic(code, name, detail, $"The environment refused the {kind} '{name}': {detail}");
    }

    private static void DiscardRefusedWorkflows(IFileAccessor workspace, ImmutableArray<WorkflowResponse> responses)
    {
        var refused = new HashSet<Guid>(responses.Where(response => !string.IsNullOrEmpty(response.ErrorMessage)).Select(response => response.WorkflowId));

        foreach (var workflow in refused.Count == 0 ? Array.Empty<WorkflowFile>() : WorkflowWorkspace.FindAll(workspace).Where(workflow => refused.Contains(workflow.WorkflowId)))
        {
            workspace.DeleteDirectory(new AgentFilePath($"{WorkflowWorkspace.WorkflowsFolderName}/{workflow.FolderName}"));
        }
    }

    private static DefinitionBase WithDeclaredCollections(DefinitionBase definition, IReadOnlyList<BotComponentCollection> declaredCollections)
    {
        if (definition is not BotDefinition bot || declaredCollections.Count == 0)
        {
            return definition;
        }

        var linked = bot.ComponentCollections.IsDefault ? ImmutableArray<BotComponentCollection>.Empty : bot.ComponentCollections;
        var missing = declaredCollections.Where(declared => !linked.Any(collection => string.Equals(collection.SchemaName.Value, declared.SchemaName.Value, StringComparison.OrdinalIgnoreCase))).ToArray();

        return missing.Length == 0 ? definition : bot.WithComponentCollections(linked.Concat(missing).ToArray());
    }

    private async Task PushComponentsAsync(ComponentPush push, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await _synchronizer.PushLocalChangesAsync(push.Folder, push.OperationContext, push.Definition, push.DataverseClient, push.SyncInfo, push.CloudFlowMetadata, push.Prompts, cancellationToken, overwriteRemoteChanges: push.Overwrite).ConfigureAwait(false);

                return;
            }
            catch (ArgumentException failure) when (failure.Message.IndexOf(UnreadableReplyMarker, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (attempt >= UnreadableReplyAttempts)
                {
                    throw new WorkspacePushException(WorkspacePushErrorCodes.ResponseUnreadable, $"The service's reply to the push could not be read after {attempt} attempt(s).", failure) { Attempts = attempt, Refusals = push.Refusals };
                }

                await TimeProviderDelay.WaitAsync(_timeProvider, TimeSpan.FromSeconds(2 * attempt), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is not OperationCanceledException and not WorkspacePushException && ConnectionReferenceFailure(failure, push.Refusals) is { } typedFailure)
            {
                if (attempt > 1)
                {
                    throw typedFailure;
                }
            }
            catch (Exception failure) when (push.Refusals.Count > 0 && failure is not OperationCanceledException and not WorkspacePushException && Chain(failure).Any(current => Names(current, MissingEntityMarker)))
            {
                throw new WorkspacePushException(WorkspacePushErrorCodes.MissingAfterRefusals, $"The push failed after the environment refused or disabled {push.Refusals.Count} custom connector(s), workflow(s) or AI prompt(s) it depends on: {DataverseFailureText.Of(failure)}", failure) { Refusals = push.Refusals };
            }

            await _retargetService.SyncWorkspaceAsync(push.Folder, push.OperationContext, null, false, push.DataverseClient, push.SyncInfo, await RereadFlowMetadataAsync(push, cancellationToken).ConfigureAwait(false), cancellationToken, await RereadPromptsAsync(push, cancellationToken).ConfigureAwait(false), syncCustomConnectors: false, syncWorkflowsAndPrompts: false).ConfigureAwait(false);
        }
    }

    private static async Task<CloudFlowMetadata> RereadFlowMetadataAsync(ComponentPush push, CancellationToken cancellationToken) => push.CloudFlowMetadata is { WorkflowsReported: true } reported ? reported : DeclaredConnectionReferences.WithProvisioned(await WorkspaceSynchronizer.GetRemoteWorkflowContentAsync(push.DataverseClient, push.SyncInfo, cancellationToken).ConfigureAwait(false), push.CloudFlowMetadata?.ConnectionReferences ?? default);

    private static async Task<ImmutableArray<AIPromptMetadata>> RereadPromptsAsync(ComponentPush push, CancellationToken cancellationToken) => push.SyncInfo.AgentId.HasValue ? push.Prompts : (await WorkspaceSynchronizer.DownloadAIPromptsAsync(push.DataverseClient, push.SyncInfo, null, cancellationToken).ConfigureAwait(false)).ToImmutableArray();

    private static WorkspacePushException? ConnectionReferenceFailure(Exception failure, IReadOnlyList<WorkspacePushDiagnostic> refusals)
    {
        if (Chain(failure).Any(IsMissingConnectionReference))
        {
            return new WorkspacePushException(WorkspacePushErrorCodes.ConnectionReferenceMissing, $"A connection reference the components use does not exist in the environment: {DataverseFailureText.Of(failure)}", failure) { Refusals = refusals };
        }

        return Chain(failure).Any(current => Names(current, DuplicateConnectionReferenceMarker)) ? new WorkspacePushException(WorkspacePushErrorCodes.ConnectionReferenceDuplicate, $"The environment already holds a connection reference the push tried to create: {DataverseFailureText.Of(failure)}", failure) { Refusals = refusals } : null;
    }

    private static bool IsMissingConnectionReference(Exception failure) => (failure is DataverseNotFoundException || (failure is DataverseException dataverseFailure && string.Equals(dataverseFailure.ErrorCodeValue, ObjectDoesNotExistCode, StringComparison.OrdinalIgnoreCase))) && Names(failure, ConnectionReferenceEntity);

    private static bool Names(Exception failure, string marker) => failure.Message.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0;

    private static IEnumerable<Exception> Chain(Exception? failure)
    {
        for (var current = failure; current is not null; current = current.InnerException)
        {
            yield return current;
        }
    }

    private async Task<ImmutableArray<string>> UploadKnowledgeFilesAsync(DirectoryPath folder, ISyncDataverseClient dataverseClient, int changeCount, CancellationToken cancellationToken)
    {
        try
        {
            return await _synchronizer.UploadKnowledgeFilesAsync(folder, dataverseClient, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            throw new WorkspacePushException(WorkspacePushErrorCodes.KnowledgeUploadFailed, $"{changeCount} component change(s) were pushed, but the knowledge files could not be uploaded: {DataverseFailureText.Of(failure)}", failure) { PushedChangeCount = changeCount };
        }
    }

    private sealed record ComponentPush(DirectoryPath Folder, AuthoringOperationContextBase OperationContext, DefinitionBase Definition, ISyncDataverseClient DataverseClient, AgentSyncInfo SyncInfo, CloudFlowMetadata? CloudFlowMetadata, ImmutableArray<AIPromptMetadata> Prompts, bool Overwrite, IReadOnlyList<WorkspacePushDiagnostic> Refusals);
}

internal sealed record AuxiliaryContent(CustomConnectorPushResult Connectors, ImmutableArray<WorkflowResponse> WorkflowResponses, ImmutableArray<AIPromptResponse> PromptResponses, CloudFlowMetadata? CloudFlowMetadata, ImmutableArray<AIPromptMetadata> Prompts)
{
    internal static readonly AuxiliaryContent None = new(new CustomConnectorPushResult(), ImmutableArray<WorkflowResponse>.Empty, ImmutableArray<AIPromptResponse>.Empty, null, ImmutableArray<AIPromptMetadata>.Empty);

    internal static async Task<AuxiliaryContent> PushAsync(IWorkspaceSynchronizer synchronizer, IFileAccessor workspace, DirectoryPath folder, ISyncDataverseClient dataverseClient, Guid? agentId, bool overwrite, WorkflowActivationMode activationMode, Action<string>? stageStarted, CancellationToken cancellationToken)
    {
        stageStarted?.Invoke(AgentPushStages.Connectors);
        var connectors = await synchronizer.PushCustomConnectorsAsync(folder, dataverseClient, cancellationToken, overwriteRemoteChanges: overwrite).ConfigureAwait(false);
        stageStarted?.Invoke(AgentPushStages.ConnectionReferences);
        var provisioned = await DeclaredConnectionReferences.ProvisionAsync(workspace, synchronizer, dataverseClient, connectors.PushedRowIds, cancellationToken).ConfigureAwait(false);

        if (!agentId.HasValue)
        {
            return None with { Connectors = connectors, CloudFlowMetadata = DeclaredConnectionReferences.WithProvisioned(null, provisioned) };
        }

        stageStarted?.Invoke(AgentPushStages.Workflows);
        var (workflowResponses, cloudFlowMetadata) = await synchronizer.UpsertWorkflowForAgentAsync(folder, dataverseClient, agentId, cancellationToken, activationMode).ConfigureAwait(false);
        stageStarted?.Invoke(AgentPushStages.Prompts);
        var (promptResponses, prompts) = await synchronizer.UpsertAIPromptsForAgentAsync(folder, dataverseClient, agentId, cancellationToken, overwriteRemoteChanges: overwrite).ConfigureAwait(false);

        return new AuxiliaryContent(connectors, workflowResponses, promptResponses, DeclaredConnectionReferences.WithProvisioned(cloudFlowMetadata, provisioned), prompts);
    }
}