// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Collections.Immutable;
using Microsoft.Agents.ObjectModel;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.CopilotStudio.Sync;

/// <summary>What attaching a workspace to an agent in a target environment needs.</summary>
public sealed class WorkspaceAttachRequest
{
    /// <summary>The workspace to attach.</summary>
    public DirectoryPath WorkspaceFolder { get; init; }

    /// <summary>The Dataverse organization of the target environment.</summary>
    public Uri? DataverseEndpoint { get; init; }

    /// <summary>The target environment.</summary>
    public string EnvironmentId { get; init; } = string.Empty;

    /// <summary>The tenant the target environment belongs to.</summary>
    public Guid TenantId { get; init; }

    /// <summary>The display name the agent is created with when the target holds none with the schema name, which the attach refuses with <see cref="AgentScaffoldException"/> before creating the agent when it holds more than <see cref="AgentWorkspaceIdentity.MaximumDisplayNameLength"/> characters.</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>The schema name of the agent to attach to.</summary>
    public string SchemaName { get; init; } = string.Empty;

    /// <summary>Whether a workspace already connected to an agent may be moved to this target.</summary>
    public bool AllowRetarget { get; init; }

    /// <summary>The agent the schema name has to belong to when the target already holds one, or <see langword="null"/> to adopt whichever agent carries it.</summary>
    public Guid? ExpectedAgentId { get; init; }

    /// <summary>Whether a retarget first writes a recovery backup of the previous binding into the workspace, for callers whose workspace outlives an interrupted operation.</summary>
    public bool PersistRetargetBackup { get; init; }

    internal bool CreateIfMissing { get; init; } = true;

    internal bool PushAuxiliaryContent { get; init; } = true;

    internal TimeSpan ProvisioningTimeout { get; init; } = AgentProvisioning.DefaultTimeout;
}

/// <summary>What attaching a workspace to an agent did.</summary>
public sealed class WorkspaceAttachResult
{
    /// <summary>The agent the workspace is now attached to.</summary>
    public Guid AgentId { get; init; }

    /// <summary>The agent's schema name, which the service may have changed when it created the agent.</summary>
    public string SchemaName { get; init; } = string.Empty;

    /// <summary>Whether the attach created the agent.</summary>
    public bool AgentCreated { get; init; }

    /// <summary>Whether the workspace was connected to another agent before.</summary>
    public bool Retargeted { get; init; }

    /// <summary>Whether an agent that already carried the schema name was adopted without the request naming it.</summary>
    public bool AdoptedExistingAgent { get; init; }

    internal AgentSyncInfo SyncInfo { get; init; } = new AgentSyncInfo();

    /// <summary>Why each custom connector the attach could not save was refused, one redacted sentence per connector.</summary>
    public IReadOnlyList<string> ConnectorFailureReasons { get; init; } = Array.Empty<string>();

    /// <summary>What the target reported for each workflow the attach upserted as a draft.</summary>
    public ImmutableArray<WorkflowResponse> WorkflowResponses { get; init; } = ImmutableArray<WorkflowResponse>.Empty;

    /// <summary>What the target reported for each AI prompt the attach upserted.</summary>
    public ImmutableArray<SyncDataverseClient.AIPromptResponse> PromptResponses { get; init; } = ImmutableArray<SyncDataverseClient.AIPromptResponse>.Empty;
}

/// <summary>What connecting a workspace and its component-collection folders to an agent that already exists needs.</summary>
public sealed class WorkspaceConnectRequest
{
    /// <summary>The agent workspace to connect.</summary>
    public DirectoryPath WorkspaceFolder { get; init; }

    /// <summary>The Dataverse organization of the agent's environment.</summary>
    public Uri? DataverseEndpoint { get; init; }

    /// <summary>The agent's environment.</summary>
    public string EnvironmentId { get; init; } = string.Empty;

    /// <summary>The tenant the environment belongs to.</summary>
    public Guid TenantId { get; init; }

    /// <summary>The schema name of the agent to connect to.</summary>
    public string SchemaName { get; init; } = string.Empty;

    /// <summary>The folder of each component collection to connect, keyed by the collection's schema name.</summary>
    public IReadOnlyDictionary<string, DirectoryPath> CollectionFolders { get; init; } = new Dictionary<string, DirectoryPath>();
}

/// <summary>What connecting a workspace to an agent that already exists did.</summary>
public sealed class WorkspaceConnectResult
{
    /// <summary>The agent the workspace is now connected to.</summary>
    public Guid AgentId { get; init; }

    /// <summary>The schema names of the component collections the environment does not hold, whose folders the connect left as they were.</summary>
    public IReadOnlyList<string> SkippedCollections { get; init; } = Array.Empty<string>();
}

/// <summary>Binds a workspace to an agent in a target environment, either attaching it, which creates the agent only when its schema name is free after pushing custom connectors, the declared connection references re-pointed at them, workflows as drafts and AI prompts, or connecting it and its component-collection folders to an agent that already exists without writing anything to the target.</summary>
public sealed class WorkspaceAttachService
{
    private static readonly AgentFilePath CloudCacheFolder = new(WorkspaceSynchronizer.HiddenRoot);

    private readonly IWorkspaceSynchronizer _synchronizer;

    private readonly IWorkspaceRetargetService _retargetService;

    private readonly IWorkspaceCloudCacheWriter _cloudCacheWriter;

    private readonly IOperationContextProvider _operationContexts;

    private readonly IFileAccessorFactory _fileAccessorFactory;

    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the service.</summary>
    /// <param name="synchronizer">Reads and checks the workspace, pushes what the agent needs and saves its sync info.</param>
    /// <param name="retargetService">Writes the cloud baseline and owns the binding snapshot of a retarget.</param>
    /// <param name="cloudCacheWriter">Writes a connected folder's cloud cache from fresh reads of the target.</param>
    /// <param name="operationContexts">Builds the authoring operation context.</param>
    /// <param name="fileAccessorFactory">Opens the workspace.</param>
    /// <param name="timeProvider">The clock that times the wait for Copilot Studio to provision the agent.</param>
    public WorkspaceAttachService(IWorkspaceSynchronizer synchronizer, IWorkspaceRetargetService retargetService, IWorkspaceCloudCacheWriter cloudCacheWriter, IOperationContextProvider operationContexts, IFileAccessorFactory fileAccessorFactory, TimeProvider timeProvider)
    {
        _synchronizer = synchronizer ?? throw new ArgumentNullException(nameof(synchronizer));
        _retargetService = retargetService ?? throw new ArgumentNullException(nameof(retargetService));
        _cloudCacheWriter = cloudCacheWriter ?? throw new ArgumentNullException(nameof(cloudCacheWriter));
        _operationContexts = operationContexts ?? throw new ArgumentNullException(nameof(operationContexts));
        _fileAccessorFactory = fileAccessorFactory ?? throw new ArgumentNullException(nameof(fileAccessorFactory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>Attaches the workspace once Copilot Studio no longer reports the agent's bot as provisioning, unless the bot was created from a CLI agent template and so is provisioned only by its first publish, renaming every name it derives from the schema name it declares after the agent's schema name, undoing its local binding changes and throwing <see cref="WorkspaceAttachException"/> that says what the target holds when a step fails.</summary>
    /// <param name="request">What to attach and where.</param>
    /// <param name="dataverseClient">The client, already targeting the environment in the request.</param>
    /// <param name="cancellationToken">Cancels the attach.</param>
    public async Task<WorkspaceAttachResult> AttachAsync(WorkspaceAttachRequest request, ISyncDataverseClient dataverseClient, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (dataverseClient is null)
        {
            throw new ArgumentNullException(nameof(dataverseClient));
        }

        if (request.DataverseEndpoint is not { } dataverseEndpoint || string.IsNullOrWhiteSpace(request.SchemaName))
        {
            throw new ArgumentException("The request has to name the target's Dataverse endpoint and the agent's schema name.", nameof(request));
        }

        AgentProvisioning.RequirePositive(request.ProvisioningTimeout, nameof(request));
        var workspaceFolder = request.WorkspaceFolder;
        var workspace = _fileAccessorFactory.Create(workspaceFolder);
        var declaredSchemaName = AgentWorkspaceIdentity.TryRead(workspace)?.SchemaName;
        var isRetarget = _synchronizer.IsSyncInfoAvailable(workspaceFolder);

        if (isRetarget && !request.AllowRetarget)
        {
            throw WorkspaceAttachException.AlreadyConnected(request.SchemaName);
        }

        if (await TryReadDefinitionAsync(workspaceFolder, cancellationToken).ConfigureAwait(false) is { } definition)
        {
            var classification = WorkspaceClassifier.Classify(definition, workspace);

            if (!classification.Allows(SyncOperation.Reattach))
            {
                throw WorkspaceAttachException.ReattachNotSupported(request.SchemaName, AuthoringSupportGate.DescribeBlocked(classification, SyncOperation.Reattach));
            }

            await _synchronizer.ThrowIfWorkspaceInvalidAsync(workspaceFolder, definition, cancellationToken).ConfigureAwait(false);
        }

        var existing = await dataverseClient.GetAgentIdBySchemaNameAsync(request.SchemaName, cancellationToken).ConfigureAwait(false);

        if (existing == Guid.Empty)
        {
            if (!request.CreateIfMissing)
            {
                throw WorkspaceAttachException.AgentNotFound(request.SchemaName, request.EnvironmentId);
            }

            AgentWorkspaceIdentity.RequireAllowedLength(request.DisplayName);
        }

        if (existing != Guid.Empty && request.ExpectedAgentId is { } expectedAgentId && expectedAgentId != existing)
        {
            throw WorkspaceAttachException.AgentAlreadyExists(request.SchemaName, existing);
        }

        RemoteBindingSnapshot? bindingSnapshot = null;
        AttachTarget? target = null;

        try
        {
            target = existing == Guid.Empty ? await CreateAgentAsync(request, workspace, dataverseClient, cancellationToken).ConfigureAwait(false) : new AttachTarget(existing, request.SchemaName, Created: false, Adopted: request.ExpectedAgentId is null);
            await AgentProvisioning.RequireProvisionedAsync(dataverseClient as IAgentProvisioningDataverseClient, target.AgentId, request.ProvisioningTimeout, _timeProvider, cancellationToken).ConfigureAwait(false);
            var syncInfo = await WorkspaceCloneSequence.ForAgentAsync(dataverseClient, target.AgentId, dataverseEndpoint, request.EnvironmentId, request.TenantId, cancellationToken).ConfigureAwait(false);
            var operationContext = await _operationContexts.GetAsync(syncInfo).ConfigureAwait(false);

            if (isRetarget)
            {
                bindingSnapshot = _retargetService.ResetRemoteBindingState(workspaceFolder);

                if (request.PersistRetargetBackup)
                {
                    _retargetService.PersistRetargetBackup(workspaceFolder, bindingSnapshot);
                }
            }
            else
            {
                _synchronizer.ClearComponentSyncBaselines(workspaceFolder);
            }

            var auxiliary = request.PushAuxiliaryContent ? await AuxiliaryContent.PushAsync(_synchronizer, workspace, workspaceFolder, dataverseClient, syncInfo.AgentId, overwrite: true, WorkflowActivationMode.DraftWhenConnectionReferencesExist, stageStarted: null, cancellationToken).ConfigureAwait(false) : AuxiliaryContent.None;

            await _retargetService.SyncWorkspaceAsync(workspaceFolder, operationContext, null, false, dataverseClient, syncInfo, auxiliary.CloudFlowMetadata, cancellationToken, auxiliary.Prompts, syncCustomConnectors: false, syncWorkflowsAndPrompts: false).ConfigureAwait(false);
            AgentSchemaNameRoot.Rebase(workspace, declaredSchemaName, target.SchemaName);
            await _synchronizer.SaveSyncInfoAsync(workspaceFolder, syncInfo).ConfigureAwait(false);

            if (isRetarget && request.PersistRetargetBackup)
            {
                _retargetService.FinalizeRetarget(workspaceFolder, pushSucceeded: true);
            }

            return new WorkspaceAttachResult
            {
                AgentId = target.AgentId,
                SchemaName = target.SchemaName,
                AgentCreated = target.Created,
                Retargeted = isRetarget,
                AdoptedExistingAgent = target.Adopted,
                SyncInfo = syncInfo,
                ConnectorFailureReasons = auxiliary.Connectors.FailureReasons.Select(DataverseFailureText.Redact).ToArray(),
                WorkflowResponses = auxiliary.WorkflowResponses,
                PromptResponses = auxiliary.PromptResponses,
            };
        }
        catch (Exception failure)
        {
            Undo(request, workspace, isRetarget, bindingSnapshot);

            if (failure is OperationCanceledException)
            {
                throw;
            }

            throw target is not null ? WorkspaceAttachException.ReattachFailed(target.AgentId, request.SchemaName, failure) : await DescribeFailedCreationAsync(request, dataverseClient, failure, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<AttachTarget> CreateAgentAsync(WorkspaceAttachRequest request, IFileAccessor workspace, ISyncDataverseClient dataverseClient, CancellationToken cancellationToken)
    {
        var created = await dataverseClient.CreateNewAgentAsync(request.DisplayName, request.SchemaName, AgentClassifier.DetectAuthoringShapeFromFolder(workspace), cancellationToken).ConfigureAwait(false);

        return new AttachTarget(created.AgentId, created.SchemaName ?? request.SchemaName, Created: true, Adopted: false);
    }

    /// <summary>Connects the workspace and the component-collection folders whose collections the environment holds to an agent that already exists, writing each folder's .mcs only from fresh reads of the environment without writing to it or touching content, refreshing a folder already connected to the same agent or collection there, refusing with <see cref="WorkspaceAttachException"/> before any write when a folder is connected elsewhere, and leaving every folder's .mcs as it was when the connect fails.</summary>
    /// <param name="request">What to connect and the agent to connect it to.</param>
    /// <param name="dataverseClient">The client, already targeting the environment in the request.</param>
    /// <param name="cancellationToken">Cancels the connect.</param>
    public async Task<WorkspaceConnectResult> ConnectAsync(WorkspaceConnectRequest request, ISyncDataverseClient dataverseClient, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (dataverseClient is null)
        {
            throw new ArgumentNullException(nameof(dataverseClient));
        }

        if (request.DataverseEndpoint is not { } dataverseEndpoint || string.IsNullOrWhiteSpace(request.EnvironmentId) || request.CollectionFolders is null || string.IsNullOrWhiteSpace(request.SchemaName) || request.CollectionFolders.Keys.Any(string.IsNullOrWhiteSpace) || request.CollectionFolders.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.CollectionFolders.Count)
        {
            throw new ArgumentException("The request has to name the agent's Dataverse endpoint and environment, the agent by schema name, and each component collection once by schema name.", nameof(request));
        }

        var folders = request.CollectionFolders.Values.Prepend(request.WorkspaceFolder).ToArray();

        if (folders.Distinct().Count() != folders.Length)
        {
            throw new ArgumentException("The workspace and every component collection need folders of their own.", nameof(request));
        }

        var agentId = await WorkspaceCloneSequence.FindAgentAsync(dataverseClient, null, request.SchemaName, _ => WorkspaceAttachException.ConnectAgentNotFound(request.SchemaName, request.EnvironmentId), cancellationToken).ConfigureAwait(false);
        var collections = request.CollectionFolders.Count == 0 ? ImmutableDictionary<string, ComponentCollectionInfo>.Empty : await ComponentCollectionFolders.LinkClientOf(dataverseClient).GetComponentCollectionsBySchemaNamesAsync(request.CollectionFolders.Keys, cancellationToken).ConfigureAwait(false);
        var syncInfo = await WorkspaceCloneSequence.ForAgentAsync(dataverseClient, agentId, dataverseEndpoint, request.EnvironmentId, request.TenantId, cancellationToken).ConfigureAwait(false);
        var targets = request.CollectionFolders.Where(collection => collections.ContainsKey(collection.Key)).Select(collection => new ConnectTarget(collection.Value, WorkspaceCloneSequence.ForComponentCollection(syncInfo, collections[collection.Key].Id))).Prepend(new ConnectTarget(request.WorkspaceFolder, syncInfo)).ToArray();
        await RefuseFoldersConnectedElsewhereAsync(request, agentId, targets).ConfigureAwait(false);
        var bindings = new List<KeyValuePair<DirectoryPath, RemoteBindingSnapshot?>>();

        try
        {
            foreach (var target in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bindings.Add(new KeyValuePair<DirectoryPath, RemoteBindingSnapshot?>(target.Folder, _fileAccessorFactory.Create(target.Folder).ListFiles(WorkspaceSynchronizer.HiddenRoot).Any() ? _retargetService.ResetRemoteBindingState(target.Folder) : null));
                await _cloudCacheWriter.WriteCloudCacheAsync(target.Folder, await _operationContexts.GetAsync(target.SyncInfo).ConfigureAwait(false), dataverseClient, target.SyncInfo, cancellationToken).ConfigureAwait(false);
                await _synchronizer.SaveSyncInfoAsync(target.Folder, target.SyncInfo).ConfigureAwait(false);
            }
        }
        catch
        {
            foreach (var binding in bindings)
            {
                RestoreBinding(binding.Key, binding.Value);
            }

            throw;
        }

        return new WorkspaceConnectResult
        {
            AgentId = agentId,
            SkippedCollections = request.CollectionFolders.Keys.Where(schemaName => !collections.ContainsKey(schemaName)).ToArray(),
        };
    }

    private async Task RefuseFoldersConnectedElsewhereAsync(WorkspaceConnectRequest request, Guid agentId, IEnumerable<ConnectTarget> targets)
    {
        var connectedElsewhere = new List<DirectoryPath>();

        foreach (var target in targets.Where(target => _synchronizer.IsSyncInfoAvailable(target.Folder)))
        {
            if (!Binds(await _synchronizer.GetSyncInfoAsync(target.Folder).ConfigureAwait(false), target.SyncInfo))
            {
                connectedElsewhere.Add(target.Folder);
            }
        }

        if (connectedElsewhere.Count > 0)
        {
            throw WorkspaceAttachException.ConnectBoundToAnotherAgent(request.SchemaName, agentId, request.EnvironmentId, connectedElsewhere);
        }
    }

    private static bool Binds(AgentSyncInfo bound, AgentSyncInfo target) => bound.AgentId == target.AgentId && bound.ComponentCollectionId == target.ComponentCollectionId && string.Equals(bound.EnvironmentId, target.EnvironmentId, StringComparison.OrdinalIgnoreCase);

    private void RestoreBinding(DirectoryPath folder, RemoteBindingSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            RemoveCloudCache(_fileAccessorFactory.Create(folder));

            return;
        }

        try
        {
            _retargetService.RestoreRemoteBindingState(folder, snapshot);

            if (!snapshot.Files.Any(file => file.Path.Equals(WorkspaceSynchronizer.ConnectionDetailsPath)))
            {
                _fileAccessorFactory.Create(folder).Delete(WorkspaceSynchronizer.ConnectionDetailsPath);
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static async Task<WorkspaceAttachException> DescribeFailedCreationAsync(WorkspaceAttachRequest request, ISyncDataverseClient dataverseClient, Exception failure, CancellationToken cancellationToken)
    {
        try
        {
            var existing = await dataverseClient.GetAgentIdBySchemaNameAsync(request.SchemaName, cancellationToken).ConfigureAwait(false);

            if (existing != Guid.Empty)
            {
                return WorkspaceAttachException.ReattachFailed(existing, request.SchemaName, failure);
            }
        }
        catch (Exception probe) when (probe is not OperationCanceledException)
        {
            return WorkspaceAttachException.AttachUncertain(request.SchemaName, request.EnvironmentId, failure);
        }

        return WorkspaceAttachException.AttachFailed(request.SchemaName, request.EnvironmentId, failure);
    }

    private async Task<DefinitionBase?> TryReadDefinitionAsync(DirectoryPath workspaceFolder, CancellationToken cancellationToken)
    {
        try
        {
            return await _synchronizer.ReadWorkspaceDefinitionAsync(workspaceFolder, cancellationToken, checkKnowledgeFiles: false).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    private void Undo(WorkspaceAttachRequest request, IFileAccessor workspace, bool isRetarget, RemoteBindingSnapshot? bindingSnapshot)
    {
        if (bindingSnapshot is not null)
        {
            _retargetService.RestoreRemoteBindingState(request.WorkspaceFolder, bindingSnapshot);

            if (request.PersistRetargetBackup)
            {
                _retargetService.ClearRetargetBackup(request.WorkspaceFolder);
            }

            return;
        }

        if (isRetarget)
        {
            return;
        }

        RemoveCloudCache(workspace);
    }

    private static void RemoveCloudCache(IFileAccessor workspace)
    {
        try
        {
            workspace.DeleteDirectory(CloudCacheFolder);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed record AttachTarget(Guid AgentId, string SchemaName, bool Created, bool Adopted);

    private sealed record ConnectTarget(DirectoryPath Folder, AgentSyncInfo SyncInfo);
}
