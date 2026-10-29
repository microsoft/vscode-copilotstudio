// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Text.RegularExpressions;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.CopilotStudio.Sync;

/// <summary>A standalone workflow as the service or the workspace holds it.</summary>
public sealed record StandaloneWorkflowDocument
{
    /// <summary>The workflow's Dataverse identifier.</summary>
    public Guid WorkflowId { get; init; }

    /// <summary>The workflow's Dataverse name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>The workflow's description, when it carries one.</summary>
    public string? Description { get; init; }

    /// <summary>Whether the workflow is running rather than sitting as a draft.</summary>
    public bool IsActivated => StateCode == 1;

    /// <summary>The <c>statecode</c> the service holds, or <see langword="null"/> when it reported none.</summary>
    public int? StateCode { get; init; }

    /// <summary>When the service last changed the workflow, when it reports one.</summary>
    public DateTimeOffset? ModifiedOn { get; init; }

    /// <summary>The connection reference logical names the workflow uses.</summary>
    public IReadOnlyList<string> ConnectionReferences { get; init; } = Array.Empty<string>();
}

/// <summary>What downloading a standalone workflow wrote to a workspace.</summary>
public sealed record StandaloneWorkflowDownload
{
    /// <summary>The workflow that was downloaded.</summary>
    public StandaloneWorkflowDocument Workflow { get; init; } = new();

    /// <summary>The workspace-relative folder the content was written to.</summary>
    public string Folder { get; init; } = string.Empty;

    /// <summary>The workspace-relative folder a rename moved the workflow out of, or <see langword="null"/> when the folder kept its name.</summary>
    public string? PreviousFolder { get; init; }

    /// <summary>Whether anything the workspace authors changed, ignoring the cloud mirror cache.</summary>
    public bool Changed { get; init; }

    public string Warning { get; init; } = string.Empty;
}

/// <summary>What downloading a standalone workflow into its own project folder achieved.</summary>
public sealed record StandaloneWorkflowProjectDownload
{
    /// <summary>What was written beneath the project folder.</summary>
    public StandaloneWorkflowDownload Download { get; init; } = new();

    /// <summary>The project folder name, relative to the root the clone was given.</summary>
    public string ProjectFolder { get; init; } = string.Empty;

    /// <summary>The absolute project root the content was written beneath.</summary>
    public string ProjectRoot { get; init; } = string.Empty;
}

/// <summary>What pulling a standalone workflow achieved.</summary>
public sealed record StandaloneWorkflowPull
{
    /// <summary>The workflow the pull targeted.</summary>
    public Guid WorkflowId { get; init; }

    /// <summary>What was written, or <see langword="null"/> when the service no longer holds the workflow.</summary>
    public StandaloneWorkflowDownload? Download { get; init; }

    /// <summary>Whether the workflow was removed locally because the service no longer holds it.</summary>
    public bool Deleted { get; init; }
}

/// <summary>What writing a standalone workflow to the service achieved.</summary>
public sealed record StandaloneWorkflowUpsert
{
    /// <summary>The workflow that was written.</summary>
    public StandaloneWorkflowDocument Workflow { get; init; } = new();

    /// <summary>Whether the row was created rather than updated.</summary>
    public bool Created { get; init; }

    /// <summary>Whether the environment refused an update, so the row was deleted and written again.</summary>
    public bool Replaced { get; init; }

    /// <summary>The connection references the push had to declare before the workflow would land.</summary>
    public IReadOnlyList<string> CreatedConnectionReferences { get; init; } = Array.Empty<string>();

    /// <summary>Whether the local workflow asked to run, even if it had to land as a draft.</summary>
    public bool ActivationRequested { get; init; }

    /// <summary>
    /// Whether <see cref="Warning"/> reports only that the workflow landed as a draft because its
    /// connections are not ready, which a later activation pass resolves by starting it.
    /// </summary>
    public bool DraftedForConnections { get; init; }

    /// <summary>What the push could not finish, or an empty string when it finished entirely.</summary>
    public string Warning { get; init; } = string.Empty;

    /// <summary>The connection references still without a connection after the push, empty once the workflow is running.</summary>
    public IReadOnlyList<string> UnboundConnectionReferences { get; init; } = Array.Empty<string>();
}

/// <summary>How a download is allowed to treat the workflow a workspace already holds.</summary>
public sealed record StandaloneWorkflowDownloadOptions
{
    /// <summary>The options a download takes when its caller names none.</summary>
    public static StandaloneWorkflowDownloadOptions Default { get; } = new();

    /// <summary>
    /// Whether the download may replace a local workflow carrying an edit the service has not seen.
    /// A download writes the whole workflow, so without this an edit nobody pushed is simply lost.
    /// </summary>
    public bool OverwriteLocalChanges { get; init; }
}

/// <summary>How a push is allowed to treat the workflow the environment already holds.</summary>
public sealed record StandaloneWorkflowUpsertOptions
{
    /// <summary>The options a push takes when its caller names none.</summary>
    public static StandaloneWorkflowUpsertOptions Default { get; } = new();

    /// <summary>Which workflows the push may leave running.</summary>
    public WorkflowActivationMode ActivationMode { get; init; } = WorkflowActivationMode.DraftWhenConnectionsUnbound;

    /// <summary>
    /// Whether a refused update may delete the cloud workflow and write it again. 
    /// </summary>
    public bool AllowDestructiveReplace { get; init; }
}

/// <summary>The Copilot Studio agents whose components use a workflow.</summary>
public sealed record StandaloneWorkflowAgentUse
{
    /// <summary>The names of the agents, or component collections, that use the workflow.</summary>
    public IReadOnlyList<string> AgentNames { get; init; } = Array.Empty<string>();

    /// <summary>The warning a delete shows about those agents, empty when no agent uses the workflow.</summary>
    public string Warning => AgentNames.Count switch
    {
        0 => string.Empty,
        1 => $"Copilot Studio agent {QuotedNames.Join(AgentNames)} uses this workflow and loses it when the workflow is deleted.",
        _ => $"Copilot Studio agents {QuotedNames.Join(AgentNames)} use this workflow and lose it when the workflow is deleted.",
    };
}

/// <summary>Reads and writes workflows outside an agent workspace, in the service and in a workspace.</summary>
public interface IStandaloneWorkflowSynchronizer
{
    /// <summary>Lists the unmanaged cloud flows an environment holds, including the ones agents use.</summary>
    /// <param name="nameFilter">Returns only workflows whose name contains this text, when supplied.</param>
    /// <param name="maximumCount">The most rows to return, or null for every row.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The workflows the environment holds, in name order.</returns>
    Task<IReadOnlyList<StandaloneWorkflowDocument>> ListAsync(string? nameFilter, int? maximumCount, CancellationToken cancellationToken);

    /// <summary>Reads one workflow from the service.</summary>
    /// <param name="workflowId">The workflow to read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The workflow, or null when the environment has no such row.</returns>
    Task<StandaloneWorkflowDocument?> GetAsync(Guid workflowId, CancellationToken cancellationToken);

    /// <summary>Reads the single workflow a workspace defines.</summary>
    /// <param name="workspace">The workspace holding the workflow.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The workflow the workspace defines.</returns>
    Task<StandaloneWorkflowDocument> ReadLocalAsync(IFileAccessor workspace, CancellationToken cancellationToken);

    /// <summary>Downloads a workflow into a workspace, overwriting whatever is there.</summary>
    /// <param name="workflowId">The workflow to download.</param>
    /// <param name="workspace">The workspace to write into.</param>
    /// <param name="environmentId">The environment being read, used only in failure messages.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    /// <param name="options">Whether a local edit the service has not seen may be replaced.</param>
    /// <returns>What was written.</returns>
    Task<StandaloneWorkflowDownload> DownloadAsync(Guid workflowId, IFileAccessor workspace, string? environmentId, CancellationToken cancellationToken, StandaloneWorkflowDownloadOptions? options = null);

    /// <summary>Pulls a workflow into a workspace, removing it locally when the service no longer holds it.</summary>
    /// <param name="workflowId">The workflow to pull.</param>
    /// <param name="workspace">The workspace to update.</param>
    /// <param name="cancellationToken">Cancels the pull.</param>
    /// <param name="options">Whether a local edit the service has not seen may be replaced.</param>
    /// <returns>What the pull wrote or removed.</returns>
    Task<StandaloneWorkflowPull> PullAsync(Guid workflowId, IFileAccessor workspace, CancellationToken cancellationToken, StandaloneWorkflowDownloadOptions? options = null);

    /// <summary>Downloads a workflow into its own project folder beneath a root, so one root may hold several.</summary>
    /// <param name="workflowId">The workflow to download.</param>
    /// <param name="root">The directory the project folder is created beneath.</param>
    /// <param name="environmentId">The environment being read, used only in failure messages.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    /// <returns>What was written, and where the project was placed.</returns>
    Task<StandaloneWorkflowProjectDownload> DownloadProjectAsync(Guid workflowId, string root, string? environmentId, CancellationToken cancellationToken);

    /// <summary>Uploads the workflow a workspace holds, creating the row when the environment has none.</summary>
    /// <param name="workspace">The workspace holding the workflow.</param>
    /// <param name="environmentId">The environment being written, used only in failure messages.</param>
    /// <param name="cancellationToken">Cancels the upload.</param>
    /// <param name="options">How the push may treat the workflow the environment already holds.</param>
    /// <returns>What was written.</returns>
    Task<StandaloneWorkflowUpsert> UpsertAsync(IFileAccessor workspace, string? environmentId, CancellationToken cancellationToken, StandaloneWorkflowUpsertOptions? options = null);

    Task RequireNoUnpulledChangesAsync(IFileAccessor workspace, string? environmentId, CancellationToken cancellationToken);

    /// <summary>Reads the service's copy of one workflow, as the service stores it.</summary>
    /// <param name="workflowId">The workflow to read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The stored metadata, or <see langword="null"/> when the environment holds none.</returns>
    Task<SyncDataverseClient.WorkflowMetadata?> ReadMetadataAsync(Guid workflowId, CancellationToken cancellationToken);

    /// <summary>Reads which Copilot Studio agents use a workflow.</summary>
    /// <param name="workflowId">The workflow to look up.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The agents that use the workflow, with the warning a delete shows about them.</returns>
    Task<StandaloneWorkflowAgentUse> GetAgentUseAsync(Guid workflowId, CancellationToken cancellationToken);

    /// <summary>Deletes a workflow, stopping it first.</summary>
    /// <param name="workflowId">The workflow to delete.</param>
    /// <param name="workspace">The workspace whose cached copy is dropped, when one is supplied.</param>
    /// <param name="cancellationToken">Cancels the deletion.</param>
    /// <returns><see langword="false"/> when the row was already absent.</returns>
    Task<bool> DeleteAsync(Guid workflowId, IFileAccessor? workspace, CancellationToken cancellationToken);
}

/// <summary>Reads and writes workflows outside an agent workspace, in the service and in a workspace.</summary>
public sealed class StandaloneWorkflowSynchronizer : IStandaloneWorkflowSynchronizer
{
    private static readonly Regex NamedConnection = new Regex(@"Connection '(?<name>[^']+)'", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly IStandaloneWorkflowDataverseClient _dataverseClient;

    /// <summary>Creates the synchronizer.</summary>
    /// <param name="dataverseClient">The service client, already pointed at an environment.</param>
    public StandaloneWorkflowSynchronizer(IStandaloneWorkflowDataverseClient dataverseClient) =>
        _dataverseClient = dataverseClient ?? throw new ArgumentNullException(nameof(dataverseClient));

    /// <inheritdoc />
    public async Task<IReadOnlyList<StandaloneWorkflowDocument>> ListAsync(string? nameFilter, int? maximumCount, CancellationToken cancellationToken) =>
        (await _dataverseClient.ListWorkflowsAsync(nameFilter, maximumCount, cancellationToken).ConfigureAwait(false))
            .Select(summary => new StandaloneWorkflowDocument
            {
                WorkflowId = summary.WorkflowId,
                Name = summary.Name ?? summary.WorkflowId.ToString(),
                Description = summary.Description,
                StateCode = summary.StateCode,
                ModifiedOn = summary.ModifiedOn,
            })
            .OrderBy(workflow => workflow.Name, StringComparer.Ordinal)
            .ToArray();

    /// <inheritdoc />
    public async Task<StandaloneWorkflowDocument?> GetAsync(Guid workflowId, CancellationToken cancellationToken) =>
        await ReadUnmanagedAsync(workflowId, null, cancellationToken).ConfigureAwait(false) is { } metadata ? Describe(metadata) : null;

    /// <inheritdoc />
    public Task<SyncDataverseClient.WorkflowMetadata?> ReadMetadataAsync(Guid workflowId, CancellationToken cancellationToken) =>
        _dataverseClient.GetWorkflowAsync(workflowId, cancellationToken);

    /// <inheritdoc />
    public async Task<StandaloneWorkflowAgentUse> GetAgentUseAsync(Guid workflowId, CancellationToken cancellationToken) => new() { AgentNames = await _dataverseClient.GetAgentsUsingWorkflowAsync(workflowId, cancellationToken).ConfigureAwait(false) };

    /// <inheritdoc />
    public async Task<StandaloneWorkflowDocument> ReadLocalAsync(IFileAccessor workspace, CancellationToken cancellationToken) =>
        Describe(await ReadFolderAsync(workspace, cancellationToken).ConfigureAwait(false));

    /// <inheritdoc />
    public async Task<StandaloneWorkflowDownload> DownloadAsync(Guid workflowId, IFileAccessor workspace, string? environmentId, CancellationToken cancellationToken, StandaloneWorkflowDownloadOptions? options = null)
    {
        if (workspace is null)
        {
            throw new ArgumentNullException(nameof(workspace));
        }

        var metadata = await FetchAsync(workflowId, environmentId, cancellationToken).ConfigureAwait(false);

        RequireNoUnpushedChanges(workspace, workflowId, metadata, options);

        var warning = await _dataverseClient.HasUnpublishedDraftAsync(workflowId, cancellationToken).ConfigureAwait(false) ? $"'{metadata.Name ?? workflowId.ToString()}' also has a draft{StandaloneWorkflowException.InEnvironment(environmentId)} that was saved in the designer but not published, so only the published version was written. Publish the draft in the designer to get it." : string.Empty;

        return Write(metadata, workspace, workflowId) with { Warning = warning };
    }

    /// <inheritdoc />
    public async Task<StandaloneWorkflowPull> PullAsync(Guid workflowId, IFileAccessor workspace, CancellationToken cancellationToken, StandaloneWorkflowDownloadOptions? options = null)
    {
        if (workspace is null)
        {
            throw new ArgumentNullException(nameof(workspace));
        }

        var metadata = await ReadUnmanagedAsync(workflowId, null, cancellationToken).ConfigureAwait(false);

        RequireNoUnpushedChanges(workspace, workflowId, metadata, options);

        if (metadata is not null)
        {
            return new StandaloneWorkflowPull { WorkflowId = workflowId, Download = Write(metadata, workspace, workflowId) };
        }

        return new StandaloneWorkflowPull { WorkflowId = workflowId, Deleted = RemoveLocal(workspace, workflowId) };
    }

    /// <summary>
    /// Refuses to write over a workflow the workspace changed and never pushed. A download replaces
    /// the whole workflow, so the only copy of an unpushed edit is the one it would overwrite. The
    /// service's own copy settles the case the cache cannot: a workspace whose cache is missing or
    /// stale still has nothing at stake when its content is what the service would write anyway.
    /// </summary>
    private static void RequireNoUnpushedChanges(IFileAccessor workspace, Guid workflowId, SyncDataverseClient.WorkflowMetadata? service, StandaloneWorkflowDownloadOptions? options)
    {
        if ((options ?? StandaloneWorkflowDownloadOptions.Default).OverwriteLocalChanges)
        {
            return;
        }

        if (WorkflowWorkspace.FindAll(workspace).FirstOrDefault(file => file.WorkflowId == workflowId) is not { } existing
            || !StandaloneWorkflowCache.HasLocalChange(workspace, existing)
            || (service is not null && !StandaloneWorkflowCache.Differs(workspace, existing, service)))
        {
            return;
        }

        throw StandaloneWorkflowException.WorkspaceHoldsUnpushedChanges(existing.FolderName);
    }

    /// <inheritdoc />
    public async Task<StandaloneWorkflowProjectDownload> DownloadProjectAsync(Guid workflowId, string root, string? environmentId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new ArgumentNullException(nameof(root));
        }

        var metadata = await FetchAsync(workflowId, environmentId, cancellationToken).ConfigureAwait(false);
        var projectFolder = WorkflowWorkspace.ResolveProjectFolder(root, metadata.Name, workflowId);
        var projectRoot = Path.Combine(root, projectFolder);

        Directory.CreateDirectory(projectRoot);

        return new StandaloneWorkflowProjectDownload
        {
            Download = Write(metadata, WorkflowWorkspace.OpenWorkspace(projectRoot), workflowId),
            ProjectFolder = projectFolder,
            ProjectRoot = projectRoot,
        };
    }

    private async Task<SyncDataverseClient.WorkflowMetadata> FetchAsync(Guid workflowId, string? environmentId, CancellationToken cancellationToken) => await ReadUnmanagedAsync(workflowId, environmentId, cancellationToken).ConfigureAwait(false) ?? throw StandaloneWorkflowException.SourceNotFound(workflowId, environmentId);

    private async Task<SyncDataverseClient.WorkflowMetadata?> ReadUnmanagedAsync(Guid workflowId, string? environmentId, CancellationToken cancellationToken)
    {
        var metadata = await _dataverseClient.GetWorkflowAsync(workflowId, cancellationToken).ConfigureAwait(false);

        return metadata is { IsManaged: true } ? throw StandaloneWorkflowException.Managed(workflowId, metadata.Name, environmentId) : metadata;
    }

    private static StandaloneWorkflowDownload Write(SyncDataverseClient.WorkflowMetadata metadata, IFileAccessor workspace, Guid workflowId)
    {
        StandaloneWorkflowCache.Write(workspace, metadata);

        var folder = WorkflowWorkspace.RelativeFolderFor(metadata.Name, workflowId);
        var previousFolder = Relocate(workspace, workflowId, folder);
        var definitionPath = $"{folder}/{WorkflowWorkspace.DefinitionFileName}";

        var definitionChanged = ConnectionReferenceStore.WriteIfChanged(workspace, new AgentFilePath(definitionPath), WorkflowDefinitionFile.Format(metadata.ClientData ?? "{}"), NoByteOrderMark);
        var metadataChanged = ConnectionReferenceStore.WriteIfChanged(workspace, new AgentFilePath($"{folder}/{WorkflowWorkspace.MetadataFileName}"), WorkflowMetadataFile.Write(metadata), NoByteOrderMark);
        var declarationChanged = WorkflowDefinitionFile.WriteConnectionReferenceDeclaration(workspace);

        return new StandaloneWorkflowDownload
        {
            Workflow = Describe(metadata),
            Folder = folder,
            PreviousFolder = previousFolder,
            Changed = previousFolder is not null || definitionChanged || metadataChanged || declarationChanged,
        };
    }

    /// <inheritdoc />
    public async Task<StandaloneWorkflowUpsert> UpsertAsync(IFileAccessor workspace, string? environmentId, CancellationToken cancellationToken, StandaloneWorkflowUpsertOptions? options = null)
    {
        var settings = options ?? StandaloneWorkflowUpsertOptions.Default;
        var metadata = await ReadFolderAsync(workspace, cancellationToken).ConfigureAwait(false);
        var local = WorkflowDefinitionFile.Read(metadata.ClientData);

        metadata.ClientData = local.Portable;
        metadata.ConnectionReferences = local.References.Select(reference => reference.LogicalName).ToList();

        var workflowName = metadata.Name ?? metadata.WorkflowId.ToString();
        var existing = await _dataverseClient.GetWorkflowAsync(metadata.WorkflowId, cancellationToken).ConfigureAwait(false);
        var created = existing is null;
        var writing = existing is null || Differs(existing, metadata, local.Canonical);

        if (writing && await _dataverseClient.HasUnpublishedDraftAsync(metadata.WorkflowId, cancellationToken).ConfigureAwait(false))
        {
            throw StandaloneWorkflowException.EnvironmentHoldsUnpublishedDraft(workflowName, environmentId);
        }

        var (declared, known) = await ProvisionConnectionReferencesAsync(local.References, cancellationToken).ConfigureAwait(false);
        var activationRequested = metadata.StateCode == 1;
        var deferredForSetup = activationRequested
            && settings.ActivationMode == WorkflowActivationMode.DraftWhenConnectionReferencesExist
            && WorkflowActivationPlanner.HasConnectionReferences(metadata);

        if (deferredForSetup)
        {
            WorkflowActivationPlanner.ApplyState(metadata, activated: false);
        }

        var unbound = WorkflowActivationPlanner.UnboundReferences(local.References.Select(reference => reference.LogicalName), WorkflowActivationPlanner.BoundReferences(known));
        var referencesBound = activationRequested && !deferredForSetup && unbound.Count == 0;
        var wasRunning = existing?.StateCode == 1;
        var stopped = false;
        var replaced = false;
        var wrote = false;
        var draftedForConnections = false;

        if (writing)
        {
            metadata.StateCode = created ? 0 : null;
            metadata.StatusCode = created ? 1 : null;

            WorkflowResponse response;
            var replaceAttempted = false;

            try
            {
                response = await WriteAsync(metadata, created, cancellationToken).ConfigureAwait(false);

                if (!created && WorkflowActivationPlanner.IsConnectionAuthorizationFailure(response.ErrorMessage))
                {
                    stopped = stopped || (await WorkflowActivationPlanner.TryStopAsync(metadata.WorkflowId, _dataverseClient, cancellationToken).ConfigureAwait(false)).Changed;

                    if (stopped)
                    {
                        response = await WriteAsync(metadata, created, cancellationToken).ConfigureAwait(false);
                        draftedForConnections = string.IsNullOrEmpty(response.ErrorMessage);
                    }
                }

                replaceAttempted = settings.AllowDestructiveReplace && !draftedForConnections && !created && WorkflowActivationPlanner.IsConnectionAuthorizationFailure(response.ErrorMessage);

                if (replaceAttempted && existing is not null)
                {
                    response = await ReplaceAsync(metadata, existing, environmentId, cancellationToken).ConfigureAwait(false);
                    replaced = string.IsNullOrEmpty(response.ErrorMessage);
                }
            }
            catch
            {
                await RestartAsync(existing, stopped).ConfigureAwait(false);

                throw;
            }

            if (response.ErrorMessage is { Length: > 0 } errorMessage)
            {
                await RestartAsync(replaceAttempted ? null : existing, stopped).ConfigureAwait(false);

                throw WorkflowActivationPlanner.IsConnectionAuthorizationFailure(errorMessage)
                    ? StandaloneWorkflowException.ConnectionsNotReady(workflowName, environmentId, NamedConnectionReference(errorMessage) ?? "a connection reference")
                    : IsUnpublishedDraftRefusal(errorMessage)
                        ? StandaloneWorkflowException.EnvironmentHoldsUnpublishedDraft(workflowName, environmentId)
                        : StandaloneWorkflowException.PushFailed(workflowName, environmentId, errorMessage);
            }

            wrote = true;

            var stillRunning = wasRunning && !stopped && !replaced;

            metadata.StateCode = stillRunning ? existing?.StateCode : 0;
            metadata.StatusCode = stillRunning ? existing?.StatusCode : 1;
        }
        else if (existing is not null)
        {
            metadata.StateCode = existing.StateCode;
            metadata.StatusCode = existing.StatusCode;
        }

        var warning = draftedForConnections ? WorkflowActivationPlanner.DescribeDraftedForConnections(workflowName) : string.Empty;
        var warnsOnlyAboutDrafting = draftedForConnections;

        if (referencesBound && metadata.StateCode != 1)
        {
            var started = await WorkflowActivationPlanner.TryStartAsync(metadata, _dataverseClient, cancellationToken).ConfigureAwait(false);
            wrote |= started.Changed;

            if (started.Changed)
            {
                WorkflowActivationPlanner.ApplyState(metadata, activated: true);
            }

            if (WorkflowActivationPlanner.DescribeUnapplied(started, workflowName, activate: true) is { Length: > 0 } startFailure)
            {
                warning = startFailure;
                warnsOnlyAboutDrafting = false;
            }
        }
        else if ((!activationRequested || deferredForSetup) && metadata.StateCode == 1)
        {
            var stopOutcome = await WorkflowActivationPlanner.TryStopAsync(metadata.WorkflowId, _dataverseClient, cancellationToken).ConfigureAwait(false);

            if (stopOutcome.Changed)
            {
                WorkflowActivationPlanner.ApplyState(metadata, activated: false);
                wrote = true;
            }
            else if (WorkflowActivationPlanner.DescribeUnapplied(stopOutcome, workflowName, activate: false) is { Length: > 0 } stopFailure)
            {
                warning = stopFailure;
                warnsOnlyAboutDrafting = false;
            }
        }

        if (warning.Length == 0 && activationRequested && metadata.StateCode != 1)
        {
            warning = deferredForSetup
                ? WorkflowActivationPlanner.DescribeDraftedForConnectionSetup(workflowName)
                : WorkflowActivationPlanner.DescribeDraftedForConnections(workflowName);

            warnsOnlyAboutDrafting = true;
        }

        await RefreshCacheAsync(workspace, metadata.WorkflowId, wrote, existing, cancellationToken).ConfigureAwait(false);

        return new StandaloneWorkflowUpsert
        {
            Workflow = Describe(metadata),
            Created = created,
            Replaced = replaced,
            CreatedConnectionReferences = declared,
            ActivationRequested = activationRequested,
            DraftedForConnections = warnsOnlyAboutDrafting && warning.Length > 0,
            Warning = warning,
            UnboundConnectionReferences = metadata.StateCode == 1 ? Array.Empty<string>() : unbound,
        };
    }

    public async Task RequireNoUnpulledChangesAsync(IFileAccessor workspace, string? environmentId, CancellationToken cancellationToken)
    {
        var local = await ReadFolderAsync(workspace, cancellationToken).ConfigureAwait(false);

        if (await _dataverseClient.GetWorkflowAsync(local.WorkflowId, cancellationToken).ConfigureAwait(false) is not { } service
            || !Differs(service, local, WorkflowDefinitionFile.Read(local.ClientData).Canonical)
            || (StandaloneWorkflowCache.Read(workspace, local.WorkflowId) is { } cached && !Differs(service, cached, WorkflowDefinitionFile.Canonicalize(cached.ClientData))))
        {
            return;
        }

        throw StandaloneWorkflowException.EnvironmentHoldsUnpulledChanges(RequireSingle(workspace).FolderName, environmentId);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(Guid workflowId, IFileAccessor? workspace, CancellationToken cancellationToken)
    {
        var deleted = await _dataverseClient.DeleteWorkflowAsync(workflowId, cancellationToken).ConfigureAwait(false);

        if (workspace is not null)
        {
            StandaloneWorkflowCache.Remove(workspace, workflowId);
        }

        return deleted;
    }

    private static readonly System.Text.Encoding NoByteOrderMark = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Replaces the cached copy with what the service holds now, never with what was just sent. The
    /// service normalizes a definition as it stores it, so caching the pushed payload would make the
    /// next comparison miss a difference that is really there. Nothing was written when the push
    /// found no difference, so the cache is already current and no read is spent.
    /// </summary>
    private async Task RefreshCacheAsync(IFileAccessor workspace, Guid workflowId, bool wrote, SyncDataverseClient.WorkflowMetadata? existing, CancellationToken cancellationToken)
    {
        if (!wrote && existing is not null)
        {
            StandaloneWorkflowCache.Write(workspace, existing);

            return;
        }

        if (await _dataverseClient.GetWorkflowAsync(workflowId, cancellationToken).ConfigureAwait(false) is { } confirmed)
        {
            StandaloneWorkflowCache.Write(workspace, confirmed);
        }
    }

    private async Task RestartAsync(SyncDataverseClient.WorkflowMetadata? existing, bool stopped)
    {
        if (existing is not null && stopped && existing.StateCode == 1)
        {
            await WorkflowActivationPlanner.TryStartAsync(existing, _dataverseClient, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static bool Differs(SyncDataverseClient.WorkflowMetadata existing, SyncDataverseClient.WorkflowMetadata local, string localDefinition) =>
        !string.Equals(WorkflowDefinitionFile.Canonicalize(existing.ClientData), localDefinition, StringComparison.Ordinal)
        || !string.Equals(existing.Name, local.Name, StringComparison.Ordinal)
        || !string.Equals(existing.Description, local.Description, StringComparison.Ordinal);

    private static bool IsUnpublishedDraftRefusal(string errorMessage) => errorMessage.IndexOf("unpublished active row", StringComparison.OrdinalIgnoreCase) >= 0;

    private async Task<WorkflowResponse> ReplaceAsync(SyncDataverseClient.WorkflowMetadata metadata, SyncDataverseClient.WorkflowMetadata existing, string? environmentId, CancellationToken cancellationToken)
    {
        try
        {
            await _dataverseClient.DeleteWorkflowAsync(metadata.WorkflowId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            return new WorkflowResponse
            {
                WorkflowId = metadata.WorkflowId,
                WorkflowName = metadata.Name ?? metadata.WorkflowId.ToString(),
                IsDisabled = true,
                ErrorMessage = failure.Message,
            };
        }

        metadata.StateCode = 0;
        metadata.StatusCode = 1;

        WorkflowResponse response;

        try
        {
            response = await _dataverseClient.InsertWorkflowAsync(metadata, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            await RestoreDeletedAsync(existing, environmentId, failure.Message).ConfigureAwait(false);

            throw;
        }

        if (response.ErrorMessage is not { Length: > 0 } errorMessage)
        {
            return response;
        }

        await RestoreDeletedAsync(existing, environmentId, errorMessage).ConfigureAwait(false);

        return response;
    }

    /// <summary>
    /// Writes back the workflow the replacement deleted. The deletion already happened, so this runs
    /// on its own token: abandoning it because the caller cancelled would leave the environment with
    /// no workflow at all.
    /// </summary>
    private async Task RestoreDeletedAsync(SyncDataverseClient.WorkflowMetadata existing, string? environmentId, string writeFailure)
    {
        var restored = await _dataverseClient.InsertWorkflowAsync(existing, CancellationToken.None).ConfigureAwait(false);

        if (restored.ErrorMessage is { Length: > 0 } errorMessage)
        {
            throw StandaloneWorkflowException.ReplaceLostTheCloudCopy(existing.Name ?? existing.WorkflowId.ToString(), environmentId, writeFailure, errorMessage);
        }
    }

    private Task<WorkflowResponse> WriteAsync(SyncDataverseClient.WorkflowMetadata metadata, bool created, CancellationToken cancellationToken) =>
        created ? _dataverseClient.InsertWorkflowAsync(metadata, cancellationToken) : _dataverseClient.UpdateWorkflowDefinitionAsync(metadata, cancellationToken);

    private static string? NamedConnectionReference(string errorMessage) =>
        NamedConnection.Match(errorMessage) is { Success: true } match ? match.Groups["name"].Value : null;

    /// <summary>
    /// Declares the connection references the definition binds that the environment does not have.
    /// The environment is asked once which of them it already holds, rather than once per reference,
    /// and that answer is returned so the activation check need not ask again.
    /// </summary>
    private async Task<(IReadOnlyList<string> Declared, IReadOnlyCollection<SyncDataverseClient.ConnectionReferenceInfo> Known)> ProvisionConnectionReferencesAsync(IReadOnlyList<WorkflowConnectionReference> references, CancellationToken cancellationToken)
    {
        if (references.Count == 0)
        {
            return (Array.Empty<string>(), Array.Empty<SyncDataverseClient.ConnectionReferenceInfo>());
        }

        var logicalNames = references.Select(reference => reference.LogicalName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var known = await _dataverseClient.GetConnectionReferencesByLogicalNamesAsync(logicalNames, cancellationToken).ConfigureAwait(false);
        var present = new HashSet<string>(known.Select(reference => reference.ConnectionReferenceLogicalName), StringComparer.OrdinalIgnoreCase);

        var created = new List<string>();

        foreach (var reference in references.Where(reference => !present.Contains(reference.LogicalName)))
        {
            if (present.Add(reference.LogicalName) && await TryDeclareAsync(reference, cancellationToken).ConfigureAwait(false))
            {
                created.Add(reference.LogicalName);
            }
        }

        return (created, known);
    }

    private async Task<bool> TryDeclareAsync(WorkflowConnectionReference reference, CancellationToken cancellationToken)
    {
        try
        {
            await _dataverseClient.EnsureConnectionReferenceExistsAsync(reference.LogicalName, WorkflowDefinitionFile.ConnectorIdPrefix + reference.ConnectorInternalId, cancellationToken).ConfigureAwait(false);

            return true;
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            return false;
        }
    }

    private static WorkflowFile RequireSingle(IFileAccessor workspace) => WorkflowWorkspace.RequireSingle(workspace, WorkflowWorkspace.WorkflowsFolderName);

    private static async Task<SyncDataverseClient.WorkflowMetadata> ReadFolderAsync(IFileAccessor workspace, CancellationToken cancellationToken)
    {
        if (workspace is null)
        {
            throw new ArgumentNullException(nameof(workspace));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var workflow = RequireSingle(workspace);
        var metadataPath = workflow.MetadataPath.ToString();

        SyncDataverseClient.WorkflowMetadata? metadata;

        try
        {
            metadata = WorkflowMetadataFile.ReadStrict(ConnectionReferenceStore.ReadTextOrNull(workspace, workflow.MetadataPath) ?? string.Empty);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            throw StandaloneWorkflowException.MetadataUnreadable(metadataPath, failure.Message);
        }

        if (metadata is null)
        {
            throw StandaloneWorkflowException.MetadataUnreadable(metadataPath, "the file is empty.");
        }

        metadata.ClientData = ConnectionReferenceStore.ReadTextOrNull(workspace, workflow.DefinitionPath);

        if (metadata.WorkflowId != workflow.WorkflowId)
        {
            throw StandaloneWorkflowException.MetadataUnreadable(metadataPath, $"it declares workflow {metadata.WorkflowId}, but the folder names {workflow.WorkflowId}.");
        }

        return WorkflowNaming.Validate(metadata.Name) is { } reason ? throw StandaloneWorkflowException.InvalidMetadataName(metadataPath, reason) : metadata;
    }

    private static string? Relocate(IFileAccessor workspace, Guid workflowId, string folder)
    {
        var targetFolderName = folder.Substring(WorkflowWorkspace.WorkflowsFolderName.Length + 1);
        var found = WorkflowWorkspace.FindAll(workspace).Where(file => file.WorkflowId == workflowId).ToList();
        var occupiesTarget = found.Any(file => string.Equals(file.FolderName, targetFolderName, StringComparison.Ordinal));
        string? previousFolder = null;

        foreach (var existing in found)
        {
            if (string.Equals(existing.FolderName, targetFolderName, StringComparison.Ordinal) || (!occupiesTarget && NamesTheSameFolder(workspace, existing.FolderName, targetFolderName, folder)))
            {
                continue;
            }

            previousFolder = $"{WorkflowWorkspace.WorkflowsFolderName}/{existing.FolderName}";
            WorkspaceFolders.Move(workspace, previousFolder, folder);
        }

        return previousFolder;
    }

    private static bool NamesTheSameFolder(IFileAccessor workspace, string existingFolderName, string targetFolderName, string folder) =>
        string.Equals(existingFolderName, targetFolderName, StringComparison.OrdinalIgnoreCase)
        && workspace.Exists(new AgentFilePath($"{folder}/{WorkflowWorkspace.DefinitionFileName}"));

    private static bool RemoveLocal(IFileAccessor workspace, Guid workflowId)
    {
        var removedCache = StandaloneWorkflowCache.Remove(workspace, workflowId);

        if (WorkflowWorkspace.FindAll(workspace).FirstOrDefault(file => file.WorkflowId == workflowId) is not { } existing)
        {
            return removedCache;
        }

        WorkspaceFolders.Remove(workspace, $"{WorkflowWorkspace.WorkflowsFolderName}/{existing.FolderName}");

        return true;
    }

    private static StandaloneWorkflowDocument Describe(SyncDataverseClient.WorkflowMetadata metadata) => new()
    {
        WorkflowId = metadata.WorkflowId,
        Name = metadata.Name ?? metadata.WorkflowId.ToString(),
        Description = metadata.Description,
        StateCode = metadata.StateCode,
        ConnectionReferences = metadata.ConnectionReferences is { Count: > 0 } declared ? declared.ToArray() : WorkflowDefinitionFile.ConnectionReferenceNames(metadata.ClientData).ToArray(),
    };
}
