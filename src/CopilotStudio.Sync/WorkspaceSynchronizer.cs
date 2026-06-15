// Copyright (C) Microsoft Corporation. All rights reserved.
// Ported from om/src/vscode/LanguageServers/PowerPlatformLS/Impl.PullAgent/Sync/WorkspaceSynchronizer.cs
// Key changes: ILspLogger → ISyncProgress, DataverseClient → ISyncDataverseClient

using Microsoft.CopilotStudio.Sync.Dataverse;
using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.ObjectModel.FileProjection;
using Microsoft.Agents.ObjectModel.Merge;
using Microsoft.Agents.ObjectModel.Yaml;
using Microsoft.Agents.Platform.Content;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using static Microsoft.CopilotStudio.Sync.Dataverse.SyncDataverseClient;

using Microsoft.CopilotStudio.McsCore;
namespace Microsoft.CopilotStudio.Sync;

/// <summary>
/// Helpers to write a <see cref="BotDefinition"/> to a file system via a
/// <see cref="IFileAccessor"/>.
/// Handles components, change tokens, etc. 
/// </summary>
internal class WorkspaceSynchronizer : IWorkspaceSynchronizer
{
    // We write internal state to our own hidden folder.
    // We can version this later by appending a version number subdir.
    private const string HiddenRoot = ".mcs";

    // Folder where workflows are stored.
    private const string WorkflowFolder = "workflows";

    // Maximum allowed size for a workflow upload 125 MB (workflow.json + metadata.yml).
    private const long MaxWorkflowUploadSizeBytes = 125L * 1024 * 1024;

    // Folder where environment variables are projected.
    private const string EnvironmentVariablesFolder = "environmentvariables";

    // Folder where custom connector definitions are projected.
    private const string ConnectorsFolder = "connectors";

    // Folder where AI Builder prompt definitions are projected.
    private const string PromptsFolder = "prompts";

    private static readonly AgentFilePath ChangeTokenPath = new AgentFilePath(HiddenRoot + "/changetoken.txt");

    /// <summary>
    /// Save a <see cref="AgentSyncInfo"/> in the hidden folder for future sync operations. 
    /// Client reads this path too, 
    /// </summary>
    private static readonly AgentFilePath ConnectionDetailsPath = new AgentFilePath(HiddenRoot + "/conn.json");

    private static readonly AgentFilePath GitIgnorePath = new AgentFilePath(HiddenRoot + "/.gitignore");

    // Capture the full BotDefinition.
    // This includes key information like:
    // - BotComponentId, Version (for later upload)
    // - original contents  - for providing diff; and reverting. 
    private static readonly AgentFilePath BotCachePath = new AgentFilePath(HiddenRoot + "/botdefinition.json");

    // Capture the full BotDefinition.
    // This includes key information like:
    // - BotComponentId, Version (for later upload)
    // - original contents  - for providing diff; and reverting. 
    private static readonly AgentFilePath OldBotCachePath = new AgentFilePath(HiddenRoot + "/botdefinition.yml");

    /// <summary>
    /// Write the top-level <see cref="BotEntity"/>.
    /// </summary>
    private static readonly AgentFilePath SettingsPath = new AgentFilePath("settings.mcs.yml");

    /// <summary>
    /// Forward-looking workspace layout marker (TDD D29). Sync overlay, generic YAML
    /// (never MCS-parsed); CLI-only; layout-only (no identity/shape echo).
    /// </summary>
    private static readonly AgentFilePath AgentSyncMarkerPath = new AgentFilePath(AgentClassifier.WorkspaceLayoutMarkerFileName);

    /// <summary>
    /// Write connection references for provisioning.
    /// </summary>
    private static readonly AgentFilePath ConnectionReferencesPath = new AgentFilePath("connectionreferences.mcs.yml");

    /// <summary>
    /// Write the top-level <see cref="GptComponentMetadata"/>. This is different than the agent files for sub agents. 
    /// </summary>
    private static readonly AgentFilePath TopAgentPath = new AgentFilePath("agent.mcs.yml");

    /// <summary>
    /// Write the top-level <see cref="BotComponentCollection"/>.
    /// </summary>
    private static readonly AgentFilePath ComponentCollectionPath = new AgentFilePath("collection.mcs.yml");

    private static readonly AgentFilePath ReferencesCollectionPath = new AgentFilePath("references.mcs.yml");

    // Knowledge-file content folders (TDD D34). Shape-keyed, NOT a migration: classic stays
    // at knowledge/files/ byte-identical; only CLI uses capabilities/knowledge/files/, mirroring
    // the projection (LspProjection.FolderToElementTypes, D21/D30). Forward slashes match the
    // projection folder and the AgentFilePath key form (IFileAccessor.ListFiles normalizes
    // separators, so this is byte-equivalent to the prior Path.Combine value in production).
    private const string KnowledgeFilesSubPath = "knowledge/files";
    private const string CliKnowledgeFilesSubPath = "capabilities/knowledge/files";

    /// <summary>
    /// Icon path within the agent.
    /// </summary>
    private static readonly AgentFilePath IconPath = new AgentFilePath("icon.png");

    private readonly IMcsFileParser _fileParser;
    private readonly IComponentPathResolver _pathResolver;
    private readonly IFileAccessorFactory _fileAccessorFactory;
    private readonly IIslandControlPlaneService _islandControlPlaneService;
    private readonly ISyncProgress _syncProgress;

    public WorkspaceSynchronizer(
        IMcsFileParser fileParser,
        IFileAccessorFactory writer,
        IIslandControlPlaneService islandControlPlanService,
        ISyncProgress syncProgress,
        IComponentPathResolver pathResolver)
    {
        _fileParser = fileParser;
        _fileAccessorFactory = writer;
        _islandControlPlaneService = islandControlPlanService;
        _syncProgress = syncProgress;
        _pathResolver = pathResolver;
    }

    public async Task CloneChangesAsync(
        DirectoryPath workspaceFolder,
        ReferenceTracker referenceTracker,
        AuthoringOperationContextBase operationContext,
        ISyncDataverseClient dataverseClient,
        AgentSyncInfo syncInfo,
        CancellationToken cancellationToken)
    {
        var fileAccessor = _fileAccessorFactory.Create(workspaceFolder);
        var changeToken = await GetChangeTokenOrNullAsync(fileAccessor, cancellationToken).ConfigureAwait(false);

        var result = await SyncWorkspaceAsync(
            workspaceFolder,
            operationContext,
            changeToken,
            updateWorkspaceDirectory: true,
            dataverseClient,
            syncInfo,
            null,
            cancellationToken).ConfigureAwait(false);

        if (result.Definition is BotComponentCollectionDefinition collection && collection.ComponentCollection is not null)
        {
            referenceTracker.MarkDeclaration(collection.GetRootSchemaName(), workspaceFolder);
        }

        // On clone, if there is no GptComponentMetadata (Agent.mcs.yml), write a default one.
        // Skip for CLI agents: agent.mcs.yml is the classic GptComponentMetadata file, and
        // fabricating a default for a CLI agent creates a phantom GptComponent (TDD D22).
        var isAgent = result.Definition is BotDefinition;
        var isCliAgentClone = result.Definition is BotDefinition bd && bd.Entity != null && IsCliAgentEntity(bd.Entity);
        if (isAgent && !isCliAgentClone && !HasGptComponentMetadata(result.Changeset))
        {
            WriteDefaultGptComponentMetadata(fileAccessor, cancellationToken);
        }
    }

    // After we clone, we'll write references.mcs.yml which can map from one workspace to another.
    // Do this in 2 passes (rather than 1) because we don't know what order the workspaces will be written in. 
    // Need to fill in the file paths for where we actually cloned to. 
    public async Task ApplyTouchupsAsync(
        DirectoryPath workspaceFolder,
        ReferenceTracker referenceTracker,
        CancellationToken cancellation)
    {
        var fileAccessor = _fileAccessorFactory.Create(workspaceFolder);

        // Do we have a references.mcs.yml?
        if (fileAccessor.Exists(ReferencesCollectionPath))
        {
            ReferencesSourceFile refs2;
            {
                using var file = fileAccessor.OpenRead(ReferencesCollectionPath);
                using var sr = new StreamReader(file);
                // No #if: small references file; boundary cancellation matches net10
                // ReadToEndAsync(CT) for our use case.
                cancellation.ThrowIfCancellationRequested();
                var yaml = await sr.ReadToEndAsync().ConfigureAwait(false);
                var refs = CodeSerializer.Deserialize<ReferencesSourceFile>(yaml);

                if (refs == null)
                {
                    return; // nop
                }
                var b1 = refs.ToBuilder();

                foreach (var item in b1.ComponentCollections)
                {
                    if (referenceTracker.TryGetComponentCollection(item.SchemaName, out var targetDirectory))
                    {
                        var relative = targetDirectory.GetRelativeFrom(workspaceFolder);

                        // Don't need both directory and schema name. 
                        item.SchemaName = new BotComponentCollectionSchemaName(string.Empty);
                        item.Directory = relative.ToString();
                    }
                }

                refs2 = b1.Build();
            } // dispose readers, release file lock so we can write. 

            {
                using var file = fileAccessor.OpenWrite(ReferencesCollectionPath);
                using var textWriter = new StreamWriter(file, Encoding.UTF8);
                CodeSerializer.SerializeWithoutKind(textWriter, refs2);
            }
        }
    }

    private static void WriteDefaultGptComponentMetadata(IFileAccessor fileAccessor, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var file = fileAccessor.OpenWrite(TopAgentPath);
        using var sw = new StreamWriter(file, Encoding.UTF8);

        // This will create empty file, but at least still have kind marker.
        var elemement = new GptComponentMetadata();

        YamlSerializer.Serialize(sw, elemement);
    }

    private static bool HasGptComponentMetadata(PvaComponentChangeSet changeSet)
    {
        // This can happen on a newly created Agent without any of the metadata properties set.
        var hasGptComponentMetadata = changeSet.BotComponentChanges.Any(change =>
            change is BotComponentInsert insert &&
            insert.Component is not null &&
            insert.Component is GptComponent);
        return hasGptComponentMetadata;
    }

    // Pull incremental changes from cloud and write to disk.
    // Return resulting new bot definition with changes applied. 
    public async Task<DefinitionBase> PullExistingChangesAsync(
        DirectoryPath workspaceFolder,
        AuthoringOperationContextBase operationContext, // includes login info
        DefinitionBase previousDefinition, // user's view
        ISyncDataverseClient dataverseClient,
        AgentSyncInfo syncInfo,
        CancellationToken cancellationToken,
        bool downloadAllKnowledgeFiles = false)
    {
        if (operationContext is BotComponentCollectionAuthoringOperationContext collectionContext)
        {
            downloadAllKnowledgeFiles = true;
        }

        var fileAccessor = _fileAccessorFactory.Create(workspaceFolder);

        var workflows = await GetWorkflowsAsync(workspaceFolder, dataverseClient, syncInfo, fileAccessor, cancellationToken).ConfigureAwait(false);
        var mergedConnectionReferences = previousDefinition.ConnectionReferences.Concat(workflows.ConnectionReferences)
               .Where(cr => !string.IsNullOrEmpty(cr.ConnectionReferenceLogicalName.Value))
               .GroupBy(cr => cr.ConnectionReferenceLogicalName.Value, StringComparer.OrdinalIgnoreCase)
               .Select(g => g.Last())
               .ToList();
        previousDefinition = previousDefinition.WithFlows(workflows.Workflows).WithConnectionReferences(mergedConnectionReferences);

        var aiPrompts = await GetAIPromptsAsync(workspaceFolder, dataverseClient, syncInfo, fileAccessor, cancellationToken).ConfigureAwait(false);
        if (!aiPrompts.IsDefaultOrEmpty)
        {
            previousDefinition = previousDefinition.WithAIModelDefinitions(BuildAIModelDefinitions(aiPrompts));
        }

        // Collect change conflicts
        var localChanges = await GetLocalChangesAsync(workspaceFolder, previousDefinition, dataverseClient, syncInfo, cancellationToken).ConfigureAwait(false);
        var remoteChanges = await GetRemoteChangesAsync(workspaceFolder, operationContext, dataverseClient, syncInfo, cancellationToken).ConfigureAwait(false);

        var remoteChangeset = remoteChanges.Item1;

        var originalSnapshot = ReadCloudCacheSnapshot(fileAccessor);

        // Apply raw changeSet on cloud cache
        var (newSnapshot, _) = UpdateCloudCache(fileAccessor, remoteChangeset, workflows, aiPrompts, agentId: syncInfo.AgentId);

        // Persist new delta token
        await WriteChangeTokenAsync(fileAccessor, remoteChangeset, cancellationToken).ConfigureAwait(false);

        // Resolve conflicting component / bot-entity edits via the shared
        // 3-way merge. CliAgentSyncSupport / Node G: this seam is identity
        // (schema-name) based and path-agnostic, so the CLI layered shape
        // flows through it unchanged — the layered files were already read
        // back into schema-name-keyed components by the Node E/F readers.
        var updatedChangeSet = ApplyThreeWayMerge(localChanges, remoteChanges, originalSnapshot);

        var deletedComponents = ImmutableArray.CreateBuilder<BotComponentBase>();
        foreach (var item in updatedChangeSet.BotComponentChanges.OfType<BotComponentDelete>())
        {
            if (originalSnapshot?.TryGetBotComponentById(item.BotComponentId, out var component) == true)
            {
                deletedComponents.Add(component);
            }
        }

        if (downloadAllKnowledgeFiles && newSnapshot != null)
        {
            var fileComponents = newSnapshot.Components.OfType<FileAttachmentComponent>().Where(c => !string.IsNullOrEmpty(c.DisplayName)).ToList();
            // #if kept: net10 uses Parallel.ForEachAsync with MaxDegreeOfParallelism=5
            // for concurrent knowledge-file downloads. netstandard2.0 has no equivalent
            // and the LCD foreach loses ~5x throughput when the agent has many knowledge
            // files. The cost is real, so we preserve the per-TFM behavior.
#if NETSTANDARD2_0
            foreach (var localComponent in fileComponents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var componentPath = new AgentFilePath(_pathResolver.GetComponentPath(localComponent, newSnapshot));
                await dataverseClient.DownloadKnowledgeFileAsync(
                    Path.Combine(workspaceFolder.ToString(), componentPath.ParentDirectoryName),
                    localComponent.Id,
                    localComponent.DisplayName ?? localComponent.Id.Value.ToString(),
                    cancellationToken
                ).ConfigureAwait(false);
            }
#else
            await Parallel.ForEachAsync(fileComponents, new ParallelOptions
            {
                MaxDegreeOfParallelism = 5,
                CancellationToken = cancellationToken
            }, async (localComponent, cancellationToken) =>
            {
                var componentPath = new AgentFilePath(_pathResolver.GetComponentPath(localComponent, newSnapshot));
                await dataverseClient.DownloadKnowledgeFileAsync(
                    Path.Combine(workspaceFolder.ToString(), componentPath.ParentDirectoryName),
                    localComponent.Id,
                    localComponent.DisplayName ?? localComponent.Id.Value.ToString(),
                    cancellationToken
                ).ConfigureAwait(false);
            }).ConfigureAwait(false);
#endif
        }

        // persist updated change set on directory
        var updatedDefinition = await UpdateWorkspaceDirectoryAsync(fileAccessor, updatedChangeSet, previousDefinition, deletedComponents.ToArray(), cancellationToken: cancellationToken).ConfigureAwait(false);

        var connectorResolvedDefinition = await ResolveConnectionReferenceConnectorIdsAsync(updatedDefinition, dataverseClient, cancellationToken).ConfigureAwait(false);
        await WriteCustomConnectorsAsync(fileAccessor, workspaceFolder, connectorResolvedDefinition, dataverseClient, cancellationToken).ConfigureAwait(false);

        return updatedDefinition;
    }

    /// <summary>
    /// Removes BotComponentUpsert entries from the changeset when the component is structurally
    /// identical to the pre-pull cloud cache snapshot. Also clears the Bot entity if it hasn't
    /// changed, preventing unconditional settings.mcs.yml overwrites.
    /// </summary>
    private static PvaComponentChangeSet FilterUnchangedComponents(PvaComponentChangeSet changeSet, DefinitionBase? originalSnapshot)
    {
        if (originalSnapshot == null)
        {
            return changeSet;
        }

        var filteredChanges = ImmutableArray.CreateBuilder<BotComponentChange>();
        var anyRemoved = false;

        foreach (var change in changeSet.BotComponentChanges)
        {
            if (change is BotComponentUpsert upsert && upsert.Component is BotComponentBase component)
            {
                if (originalSnapshot.TryGetComponentBySchemaName(component.SchemaNameString, out var originalComponent))
                {
                    var r1 = component.RootElement;
                    var r2 = originalComponent.RootElement;
                    if (r1 is not null && r2 is not null && r1.Equals(r2, NodeComparison.Structural))
                    {
                        anyRemoved = true;
                        continue;
                    }
                }
            }

            filteredChanges.Add(change);
        }

        // Filter out the Bot entity if it hasn't changed from the snapshot.
        // Compare both settings properties AND IconBase64 — an icon-only change
        // must not be filtered out, or WriteBotEntityAsync will write the old icon.
        var filteredBot = changeSet.Bot;
        if (filteredBot != null && originalSnapshot is BotDefinition originalBotDef && originalBotDef.Entity != null)
        {
            var originalSettings = originalBotDef.Entity.WithOnlySettingsYamlProperties();
            var newSettings = filteredBot.WithOnlySettingsYamlProperties();
            if (originalSettings.Equals(newSettings, NodeComparison.Structural)
                && originalBotDef.Entity.IconBase64 == filteredBot.IconBase64)
            {
                filteredBot = null;
                anyRemoved = true;
            }
        }

        if (!anyRemoved)
        {
            return changeSet;
        }

        var builder = changeSet.ToBuilder();
        builder.BotComponentChanges.Clear();
        foreach (var change in filteredChanges)
        {
            builder.BotComponentChanges.Add(change);
        }
        var result = builder.Build();
        if (filteredBot == null)
        {
            result = result.WithBot(null);
        }
        return result;
    }

    /// <summary>
    /// CliAgentSyncSupport / Node G: the 3-way merge orchestration extracted
    /// from <see cref="PullExistingChangesAsync"/> so it is testable in
    /// isolation (the pull entry point mixes real-filesystem workflow / AI
    /// prompt handling with the in-memory accessor, which makes it
    /// impractical to drive end-to-end in a unit test).
    /// </summary>
    /// <remarks>
    /// This is a behaviour-preserving extraction: it computes the same
    /// conflict set and applies the same per-component and bot-entity merges
    /// the inline block did, then runs <see cref="FilterUnchangedComponents"/>.
    /// It is a pure transform over (local changes, remote changes, original
    /// snapshot) — it performs no I/O, does not touch the cloud cache or
    /// change token, and does not handle workflows / AI prompts / knowledge
    /// downloads (those stay in the caller). The merge is identity (schema
    /// name) based: connection references and other non-<c>BotComponent</c>
    /// changes are NOT 3-way merged here (the remote changeset's entries pass
    /// through), matching the classic-shape behaviour.
    /// </remarks>
    internal PvaComponentChangeSet ApplyThreeWayMerge(
        (PvaComponentChangeSet ChangeSet, ImmutableArray<Change> Changes) localChanges,
        (PvaComponentChangeSet ChangeSet, ImmutableArray<Change> Changes) remoteChanges,
        DefinitionBase? originalSnapshot)
    {
        var localChangesWithoutKnowledgeFiles = localChanges.Changes
            .Where(c => c.ChangeKind != BotElementKind.FileAttachmentComponent.ToString())
            .ToImmutableArray();

        var conflictingChanges = GetConflicts(localChangesWithoutKnowledgeFiles, remoteChanges.Changes);

        var remoteChangeset = remoteChanges.ChangeSet;
        var updatedChangeSetBuilder = remoteChangeset.ToBuilder();
        if (conflictingChanges.Length != 0)
        {
            //  Apply 3-way diff on conflicting items and update changeSet
            foreach (var schemaName in conflictingChanges)
            {
                var localChange = localChanges.ChangeSet.BotComponentChanges.OfType<BotComponentUpsert>().FirstOrDefault(c => c.Component?.SchemaNameString == schemaName)?.Component;

                var remoteChange = remoteChangeset.BotComponentChanges.OfType<BotComponentUpsert>().FirstOrDefault(c => c.Component?.SchemaNameString == schemaName);
                var remoteChangeComponent = remoteChange?.Component;
                BotComponentBase? originalComponent = null;
                if (originalSnapshot?.TryGetComponentBySchemaName(schemaName, out var component) == true)
                {
                    originalComponent = component;
                }


                // If both are deletes, there is nothing to merge
                if (localChange == null && remoteChange == null)
                {
                    continue;
                }

                // Merge changes into a new component
                var updatedComponent = MergeComponent(schemaName, originalComponent, localChange, remoteChangeComponent);

                // Update change set with new component
                if (remoteChange != null)
                {
                    updatedChangeSetBuilder.BotComponentChanges.Remove(remoteChange);
                }

                if (localChange == null)
                {
                    updatedChangeSetBuilder.BotComponentChanges.Add(new BotComponentInsert(updatedComponent));
                }
                else
                {
                    updatedChangeSetBuilder.BotComponentChanges.Add(new BotComponentUpdate(updatedComponent));
                }
            }
        }

        PvaComponentChangeSet updatedChangeSet;

        // Conflict on Bot Entity
        if (localChanges.ChangeSet.Bot != null && remoteChanges.ChangeSet.Bot != null && localChanges.ChangeSet.Bot.Version != remoteChanges.ChangeSet.Bot.Version)
        {
            var originalEntity = (originalSnapshot as BotDefinition)?.Entity;
            var originalComponentYaml = originalEntity == null ? null : GetMcsYaml(originalEntity.WithOnlySettingsYamlProperties());
            var localYaml = localChanges.ChangeSet.Bot == null ? null : GetMcsYaml(localChanges.ChangeSet.Bot);
            var remoteYaml = remoteChanges.ChangeSet.Bot == null ? null : GetMcsYaml(remoteChanges.ChangeSet.Bot.WithOnlySettingsYamlProperties());

            var updatedEntityString = MergeStrings(originalComponentYaml, localYaml, remoteYaml);

            // remoteChanges.ChangeSet.Bot is non-null — guarded by the if-condition above
            var remoteBot = remoteChanges.ChangeSet.Bot!;
            var bot = CodeSerializer.Deserialize<BotEntity>(updatedEntityString) ?? remoteBot;
            // The 3-way merge operates on settings YAML only (WithOnlySettingsYamlProperties
            // strips IconBase64 and other metadata from original/remote). Restore non-settings
            // properties — including IconBase64 — from the remote bot.
            bot = remoteBot.ApplySettingsYamlProperties(bot);
            updatedChangeSetBuilder.Bot = bot;
            updatedChangeSet = updatedChangeSetBuilder.Build();
        }
        else
        {
            updatedChangeSet = updatedChangeSetBuilder.Build().WithBot(remoteChanges.ChangeSet.Bot);
        }

        // Filter out components that are identical to the pre-pull cloud cache snapshot.
        // This prevents silently overwriting local edits when the server returns a full
        // component set (non-delta) instead of just the changed components.
        updatedChangeSet = FilterUnchangedComponents(updatedChangeSet, originalSnapshot);

        return updatedChangeSet;
    }

    internal string MergeStrings(
      string? original,
      string? local,
      string? remote)
    {
        var encoding = Encoding.UTF8;

        var diffOption = new DiffOptions();
        diffOption.Flags |= DiffOptionFlags.IgnoreWhiteSpace;

        var originalFile = DiffFile.Create(
            new StringReader(original ?? string.Empty),
            encoding,
            diffOption);

        var localFile = DiffFile.Create(
            new StringReader(local ?? string.Empty),
            encoding,
            diffOption);

        var remoteDiff = DiffFile.Create(
            new StringReader(remote ?? string.Empty),
            encoding,
            diffOption);

        var mergeOptions = new MergeOptions();
        mergeOptions.Flags |= DiffOptionFlags.IgnoreWhiteSpace;
        var comparer = new DiffLineComparer(mergeOptions);

        var mergeList = MergeFinder.Merge(originalFile, localFile, remoteDiff, comparer, mergeOptions);

        using var writer = new StringWriter();

        var mergeOutput = new MergeOutput(mergeOptions, writer);
        mergeOutput.Output(originalFile, localFile, remoteDiff, mergeList);
        return writer.ToString();
    }

    internal BotComponentBase? MergeComponent(
        string schemaName,
        BotComponentBase? originalComponent,
        BotComponentBase? localChange,
        BotComponentBase? remoteChange)
    {
        // originalComponent contains DisplayName/Description in the Component, content with syntax in the RootElement
        // localChange will only have correct syntax on the RootElement
        // remoteChange contains DisplayName/Description in the Component, content with syntax in the RootElement
        // steps:
        // - create a McsYml file content for localChange and remoteChange
        // - merge the file

        var localChangeYaml = localChange?.RootElement == null ? null : CodeSerializer.Serialize(localChange.RootElement);
        var originalComponentYaml = GetMcsYaml(originalComponent);
        var remoteChangeYaml = GetMcsYaml(remoteChange);

        var mergedString = MergeStrings(originalComponentYaml, localChangeYaml, remoteChangeYaml);
        var mergedContent = CodeSerializer.Deserialize(mergedString, originalComponent?.RootElement?.GetType() ?? localChange?.RootElement?.GetType() ?? remoteChange?.RootElement?.GetType() ?? typeof(BotElement), null);
        var mergedMetaDisplayName = MergeMetaInfo(originalComponent?.DisplayName, localChange?.DisplayName, remoteChange?.DisplayName);
        var mergedMetaDescription = MergeMetaInfo(originalComponent?.Description, localChange?.Description, remoteChange?.Description);

        var (component, error) = _fileParser.CompileFileModel(schemaName, mergedContent, mergedMetaDisplayName, mergedMetaDescription);

        if (error != null)
        {
            throw error;
        }

        if (component == null)
        {
            throw new ArgumentException($"Unable to merge local and remote changes");
        }

        return component.WithId(
            remoteChange?.Id.HasValue == true
            ? remoteChange.Id
            : originalComponent?.Id.HasValue == true
                ? originalComponent.Id
                : Guid.Empty).WithVersion(remoteChange?.Version ?? localChange?.Version ?? 0);
    }

    internal ImmutableArray<string> GetConflicts(ImmutableArray<Change> local, ImmutableArray<Change> remote)
    {
        return local.Select(m => m.SchemaName).Intersect(remote.Select(r => r.SchemaName)).ToImmutableArray();
    }

    private static string? MergeMetaInfo(string? originalMeta, string? localMeta, string? remoteMeta)
    {
        if (localMeta == remoteMeta)
        {
            return localMeta;
        }

        if (originalMeta == localMeta)
        {
            return remoteMeta;
        }

        if (originalMeta == remoteMeta)
        {
            return localMeta;
        }

        return remoteMeta;
    }

    public async Task<PushChangesetResult> PushChangesetAsync(
        DirectoryPath workspaceFolder,
        AuthoringOperationContextBase operationContext, // includes login info
        PvaComponentChangeSet pushChangeset, // local changes to push up.
        ISyncDataverseClient dataverseClient,
        Guid? agentId,
        CloudFlowMetadata? cloudFlowMetadata,
        ImmutableArray<AIPromptMetadata> aiPrompts,
        CancellationToken cancellationToken,
        bool uploadAllKnowledgeFiles = false)
    {
        // CliAgentSyncSupport / Node Q2 (TDD D29 evolution contract): fail closed if the
        // workspace declares an UNKNOWN-higher layoutVersion than this tooling supports.
        // Packing/pushing a newer layout we cannot interpret could relocate or drop files,
        // so refuse rather than risk corruption (best-effort at most on read, never on write).
        var layoutGateAccessor = _fileAccessorFactory.Create(workspaceFolder);
        if (CliAgentBotEntityReader.HasUnsupportedHigherLayoutVersion(layoutGateAccessor, out var declaredLayoutVersion))
        {
            throw new InvalidOperationException(
                $"Workspace uses a newer layout (layoutVersion {declaredLayoutVersion}) than this tooling supports " +
                $"(up to {AgentClassifier.CurrentLayoutVersion}). Update the Copilot Studio tooling to push this workspace.");
        }

        if (operationContext is BotComponentCollectionAuthoringOperationContext collectionContext)
        {
            uploadAllKnowledgeFiles = true;
        }

        // Upload will atomically:
        //  - send up changes,
        //  - receive new changes - including "confirmation" changes for the *files we just updated*
        // - This will include new version numbers (especially for newly created components) and new changetoken.
        var changeset = await _islandControlPlaneService.SaveChangesAsync(
            operationContext,
            pushChangeset,
            cancellationToken).ConfigureAwait(false);

        await PushCustomConnectorsAsync(workspaceFolder, dataverseClient, cancellationToken).ConfigureAwait(false);

        await WriteChangeSetAsync(workspaceFolder, changeset, cloudFlowMetadata, aiPrompts, agentId, cancellationToken).ConfigureAwait(false);

        var fileAccessor = _fileAccessorFactory.Create(workspaceFolder);
        var postPushSnapshot = ReadCloudCacheSnapshot(fileAccessor);
        if (postPushSnapshot != null)
        {
            await WriteCustomConnectorsAsync(fileAccessor, workspaceFolder, postPushSnapshot, dataverseClient, cancellationToken).ConfigureAwait(false);
        }

        if (!uploadAllKnowledgeFiles)
        {
            return new PushChangesetResult
            {
                UploadedKnowledgeFileCount = 0,
            };
        }

        // Upload all knowledge files
        var snapshot = ReadCloudCacheSnapshot(fileAccessor);
        var numberOfUploadedFiles = 0;

        if (snapshot != null)
        {
            var newFileComponents = snapshot.Components.OfType<FileAttachmentComponent>().ToList();

            if (newFileComponents.Count == 0)
            {
                return new PushChangesetResult
                {
                    UploadedKnowledgeFileCount = 0,
                };
            }

            // #if kept: net10 uses Parallel.ForEachAsync with MaxDegreeOfParallelism=5
            // for concurrent knowledge-file uploads; netstandard2.0 has no equivalent
            // and the LCD foreach loses ~5x throughput. Cost is real, so we preserve
            // per-TFM behavior.
#if NETSTANDARD2_0
            foreach (var newFileComponent in newFileComponents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(newFileComponent.DisplayName))
                {
                    continue;
                }

                var componentPath = new AgentFilePath(_pathResolver.GetComponentPath(newFileComponent, snapshot));
                if (!IsValidFileToUpload(fileAccessor, componentPath))
                {
                    continue;
                }

                // ns2.0 BCL's IsNullOrEmpty lacks NotNullWhen; ! is compile-time only.
                await dataverseClient.UploadKnowledgeFileAsync(
                    Path.Combine(workspaceFolder.ToString(), componentPath.ParentDirectoryName),
                    newFileComponent.Id.Value,
                    newFileComponent.DisplayName!,
                    cancellationToken
                ).ConfigureAwait(false);

                numberOfUploadedFiles++;
            }
#else
            await Parallel.ForEachAsync(newFileComponents, new ParallelOptions
            {
                MaxDegreeOfParallelism = 5,
                CancellationToken = cancellationToken
            },
            async (newFileComponent, cancellationToken) =>
            {
                if (string.IsNullOrEmpty(newFileComponent.DisplayName))
                {
                    return;
                }

                var componentPath = new AgentFilePath(_pathResolver.GetComponentPath(newFileComponent, snapshot));
                if (!IsValidFileToUpload(fileAccessor, componentPath))
                {
                    return;
                }

                await dataverseClient.UploadKnowledgeFileAsync(
                    Path.Combine(workspaceFolder.ToString(), componentPath.ParentDirectoryName),
                    newFileComponent.Id.Value,
                    newFileComponent.DisplayName,
                    cancellationToken
                ).ConfigureAwait(false);

                numberOfUploadedFiles++;
            }).ConfigureAwait(false);
#endif
        }

        return new PushChangesetResult
        {
            UploadedKnowledgeFileCount = numberOfUploadedFiles,
        };
    }

    /// <summary>
    /// Pushes all local component changes to the cloud, creating newly added sub-agents if needed.
    /// </summary>
    /// <param name="workspaceFolder">The location of the root of the workspace.</param>
    /// <param name="operationContext">Information about the authoring operation.</param>
    /// <param name="workspaceDefinition">The current state of the workspace to push.</param>
    /// <param name="dataverseClient">The dataverse client used for communication with the service.</param>
    /// <param name="syncInfo">Synchronization information for the agent.</param>
    /// <param name="cloudFlowMetadata">Cloud flow metadata to project into the cloud cache.</param>
    /// <param name="aiPrompts">AI Builder prompt metadata to project into the cloud cache.</param>
    /// <param name="cancellationToken">Used to cancel the request.</param>
    public async Task PushLocalChangesAsync(
        DirectoryPath workspaceFolder,
        AuthoringOperationContextBase operationContext,
        DefinitionBase workspaceDefinition,
        ISyncDataverseClient dataverseClient,
        AgentSyncInfo syncInfo,
        CloudFlowMetadata? cloudFlowMetadata,
        ImmutableArray<AIPromptMetadata> aiPrompts,
        CancellationToken cancellationToken)
    {
        const int maxPasses = 32;
        for (var pass = 0; pass < maxPasses; pass++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fileAccessor = _fileAccessorFactory.Create(workspaceFolder);
            var cloudSnapshot = ReadCloudCacheSnapshot(fileAccessor) ?? throw new InvalidOperationException("Unable to read cloud cache from .mcs/botdefinition.json");
            var changeToken = await GetChangeTokenOrNullAsync(fileAccessor, cancellationToken).ConfigureAwait(false);
            var effectiveDefinition = OverlayCliConnectionReferences(workspaceDefinition, fileAccessor, cancellationToken);
            effectiveDefinition = await ResolveConnectionReferenceConnectorIdsAsync(effectiveDefinition, dataverseClient, cancellationToken).ConfigureAwait(false);

            var (changeSet, changes) = GetLocalChanges(effectiveDefinition, cloudSnapshot, fileAccessor, changeToken, isRemoteChange: false, deferMissingParents: true, out var deferredMissingParent);

            if (changes.IsEmpty)
            {
                if (deferredMissingParent)
                {
                    throw new InvalidOperationException("Unsupported sync operation. A component references a parent agent that could not be created on the cloud.");
                }

                break;
            }

            if (!changes.Any(c => c.SchemaName == "entity" || c.SchemaName == "icon"))
            {
                changeSet = changeSet.WithBot(null);
            }
            else if (changeSet.Bot != null)
            {
                var cloudEntity = (cloudSnapshot as BotDefinition)?.Entity;
                if (cloudEntity != null && changeSet.Bot.Version != cloudEntity.Version)
                {
                    var entityBuilder = changeSet.Bot.ToBuilder();
                    entityBuilder.Version = cloudEntity.Version;
                    changeSet = changeSet.WithBot(entityBuilder.Build());
                }
            }

            await PushChangesetAsync(workspaceFolder, operationContext, changeSet, dataverseClient, syncInfo.AgentId, cloudFlowMetadata, aiPrompts, cancellationToken).ConfigureAwait(false);

            if (!deferredMissingParent)
            {
                break;
            }

            if (pass == maxPasses - 1)
            {
                throw new InvalidOperationException("Unsupported sync operation. A component references a parent agent that could not be created on the cloud.");
            }
        }
    }

    public virtual async Task ProvisionConnectionReferencesAsync(
        DirectoryPath workspaceFolder,
        DefinitionBase definition,
        ISyncDataverseClient dataverseClient,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, Guid>? pushedConnectorIds = null)
    {
        var fileAccessor = _fileAccessorFactory.Create(workspaceFolder);
        var effectiveDefinition = OverlayCliConnectionReferences(definition, fileAccessor, cancellationToken);
        await ProvisionConnectionReferencesAsync(effectiveDefinition, dataverseClient, cancellationToken, pushedConnectorIds).ConfigureAwait(false);
    }

    public virtual async Task ProvisionConnectionReferencesAsync(
        DefinitionBase definition,
        ISyncDataverseClient dataverseClient,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, Guid>? pushedConnectorIds = null)
    {
        var connectionRefs = definition.ConnectionReferences;

        var prefixCache = new Dictionary<string, CustomConnectorMetadata[]>(StringComparer.OrdinalIgnoreCase);

        foreach (var connRef in connectionRefs)
        {
            try
            {
                var originalConnectorId = connRef.ConnectorId.ToString();
                var originalInternalId = SyncDataverseClient.ExtractConnectorInternalId(originalConnectorId);

                Guid? customConnectorRowId = null;
                if (pushedConnectorIds != null && !string.IsNullOrWhiteSpace(originalInternalId) && pushedConnectorIds.TryGetValue(originalInternalId!, out var mapped))
                {
                    customConnectorRowId = mapped;
                }

                var connectorId = await ResolveTargetConnectorIdAsync(originalConnectorId, dataverseClient, cancellationToken, prefixCache).ConfigureAwait(false);

                await dataverseClient.EnsureConnectionReferenceExistsAsync(
                    connRef.ConnectionReferenceLogicalName.ToString(),
                    connectorId,
                    cancellationToken,
                    customConnectorRowId).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _syncProgress.Report($"Failed to provision connection '{connRef.ConnectionReferenceLogicalName}': {ex.Message}");
                // Continue with other connections
            }
        }
    }

    /// <summary>
    /// Returns connection reference the agent declares, annotated with the connection it is currently bound to in Dataverse (if any).
    /// </summary>
    public virtual async Task<IReadOnlyList<ConnectionNeeded>> GetAgentConnectionReferencesAsync(DirectoryPath workspaceFolder, DefinitionBase definition, ISyncDataverseClient dataverseClient, CancellationToken cancellationToken)
    {
        var fileAccessor = _fileAccessorFactory.Create(workspaceFolder);
        var effectiveDefinition = OverlayCliConnectionReferences(definition, fileAccessor, cancellationToken);
        return await GetAgentConnectionReferencesAsync(effectiveDefinition, dataverseClient, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns connection reference declared by the bot definition, annotated with its current Dataverse binding state.
    /// </summary>
    public virtual async Task<IReadOnlyList<ConnectionNeeded>> GetAgentConnectionReferencesAsync(DefinitionBase definition, ISyncDataverseClient dataverseClient, CancellationToken cancellationToken)
    {
        var connectionRefs = definition.ConnectionReferences;
        if (connectionRefs.IsDefaultOrEmpty)
        {
            return Array.Empty<ConnectionNeeded>();
        }

        var logicalNames = connectionRefs
            .Select(cr => cr.ConnectionReferenceLogicalName.ToString())
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (logicalNames.Count == 0)
        {
            return Array.Empty<ConnectionNeeded>();
        }

        var cloudRefs = await dataverseClient.GetConnectionReferencesByLogicalNamesAsync(logicalNames, cancellationToken).ConfigureAwait(false);
        var boundIdByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var info in cloudRefs)
        {
            if (string.IsNullOrWhiteSpace(info.ConnectionReferenceLogicalName))
            {
                continue;
            }

            boundIdByName[info.ConnectionReferenceLogicalName] = info.ConnectionId ?? string.Empty;
        }

        var result = new List<ConnectionNeeded>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var prefixCache = new Dictionary<string, CustomConnectorMetadata[]>(StringComparer.OrdinalIgnoreCase);

        foreach (var connRef in connectionRefs)
        {
            var logicalName = connRef.ConnectionReferenceLogicalName.ToString();
            if (string.IsNullOrWhiteSpace(logicalName) || !seen.Add(logicalName))
            {
                continue;
            }

            boundIdByName.TryGetValue(logicalName, out var boundConnectionId);
            var connectorId = await ResolveTargetConnectorIdAsync(connRef.ConnectorId.ToString(), dataverseClient, cancellationToken, prefixCache).ConfigureAwait(false);
            var connectorName = SyncDataverseClient.ExtractConnectorInternalId(connectorId) ?? string.Empty;

            result.Add(new ConnectionNeeded
            {
                ConnectionReferenceLogicalName = logicalName,
                ConnectorId = connectorId,
                ConnectorName = connectorName,
                BoundConnectionId = boundConnectionId ?? string.Empty,
            });
        }

        return result;
    }

    /// <summary>
    /// Resolves each connection reference's connector id to the target environment.
    /// </summary>
    private static async Task<DefinitionBase> ResolveConnectionReferenceConnectorIdsAsync(DefinitionBase definition, ISyncDataverseClient dataverseClient, CancellationToken cancellationToken)
    {
        if (definition.ConnectionReferences.IsDefaultOrEmpty)
        {
            return definition;
        }

        var resolved = ImmutableArray.CreateBuilder<ConnectionReference>(definition.ConnectionReferences.Length);
        var changed = false;
        var prefixCache = new Dictionary<string, CustomConnectorMetadata[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var connRef in definition.ConnectionReferences)
        {
            var originalConnectorId = connRef.ConnectorId.ToString();
            var targetConnectorId = await ResolveTargetConnectorIdAsync(originalConnectorId, dataverseClient, cancellationToken, prefixCache).ConfigureAwait(false);
            if (string.Equals(originalConnectorId, targetConnectorId, StringComparison.Ordinal))
            {
                resolved.Add(connRef);
                continue;
            }

            var builder = connRef.ToBuilder();
            builder.ConnectorId = targetConnectorId;
            resolved.Add(builder.Build());
            changed = true;
        }

        return changed ? definition.WithConnectionReferences(resolved.ToImmutable()) : definition;
    }

    /// <summary>
    /// Rewrites a full connector id so its internal-id segment points at the target environment's connector when the connector is environment-specific.
    /// </summary>
    private static async Task<string> ResolveTargetConnectorIdAsync(string connectorId, ISyncDataverseClient dataverseClient, CancellationToken cancellationToken, Dictionary<string, CustomConnectorMetadata[]>? prefixCache = null)
    {
        var connectorName = SyncDataverseClient.ExtractConnectorInternalId(connectorId) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(connectorName))
        {
            return connectorId;
        }

        var resolvedConnectorName = await ResolveTargetConnectorNameAsync(connectorName, dataverseClient, cancellationToken, prefixCache).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(resolvedConnectorName) || string.Equals(resolvedConnectorName, connectorName, StringComparison.OrdinalIgnoreCase))
        {
            return connectorId;
        }

        var slash = connectorId.LastIndexOf('/');
        return slash >= 0 ? connectorId.Substring(0, slash + 1) + resolvedConnectorName! : resolvedConnectorName!;
    }

    /// <summary>
    /// Resolves the environment-specific connector internal id for a connector whose trailing hash differs between environments.
    /// </summary>
    private static async Task<string?> ResolveTargetConnectorNameAsync(string connectorInternalId, ISyncDataverseClient dataverseClient, CancellationToken cancellationToken, Dictionary<string, CustomConnectorMetadata[]>? prefixCache = null)
    {
        var prefix = GetStableConnectorPrefix(connectorInternalId);
        if (prefix == null)
        {
            return connectorInternalId;
        }

        CustomConnectorMetadata[] matches;
        if (prefixCache != null && prefixCache.TryGetValue(prefix, out var cached))
        {
            matches = cached;
        }
        else
        {
            matches = await dataverseClient.GetConnectorsByInternalIdPrefixAsync(prefix, cancellationToken).ConfigureAwait(false);
            if (prefixCache != null)
            {
                prefixCache[prefix] = matches;
            }
        }

        if (matches.Length == 0)
        {
            return connectorInternalId;
        }

        var exact = matches.FirstOrDefault(m => string.Equals(m.ConnectorInternalId, connectorInternalId, StringComparison.OrdinalIgnoreCase));
        if (exact != null)
        {
            return exact.ConnectorInternalId;
        }

        var newest = matches
            .Where(m => !string.IsNullOrWhiteSpace(m.ConnectorInternalId))
            .OrderByDescending(m => m.ModifiedOn)
            .FirstOrDefault();

        return newest?.ConnectorInternalId ?? connectorInternalId;
    }

    /// <summary>
    /// Returns the stable connector internal id prefix for an environment-specific connector whose trailing segment is a 16-character hex hash; otherwise returns null.
    /// </summary>
    private static string? GetStableConnectorPrefix(string? connectorInternalId)
    {
        if (string.IsNullOrWhiteSpace(connectorInternalId))
        {
            return null;
        }

        const string separator = "-5f";
        var idx = connectorInternalId!.LastIndexOf(separator, StringComparison.Ordinal);
        if (idx <= 0)
        {
            return null;
        }

        var tail = connectorInternalId.Substring(idx + separator.Length);
        if (tail.Length != 16 || !tail.All(Uri.IsHexDigit))
        {
            return null;
        }

        return connectorInternalId.Substring(0, idx + separator.Length);
    }

    /// <summary>
    /// Sync workspace to write bot definition, git ignore, change token files in .mcs.
    /// </summary>
    /// <param name="workspaceFolder">Workspace folder.</param>
    /// <param name="operationContext">Context.</param>
    /// <param name="changeToken">Change token.</param>
    /// <param name="updateWorkspaceDirectory">Whether to update workspace directory.</param>
    /// <param name="dataverseClient">The dataverse client to use for communication with the dataverse service.</param>
    /// <param name="syncInfo">Synchronization information for the agent.</param>
    /// <param name="cloudFlowMetadata">The cloud flow metadata containing workflow definitions and connection references.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    /// <returns>Workspace sync result.</returns>
    public async Task<WorkspaceSyncInfo> SyncWorkspaceAsync(
        DirectoryPath workspaceFolder,
        AuthoringOperationContextBase operationContext,
        string? changeToken,
        bool updateWorkspaceDirectory,
        ISyncDataverseClient dataverseClient,
        AgentSyncInfo syncInfo,
        CloudFlowMetadata? cloudFlowMetadata,
        CancellationToken cancellationToken)
    {
        var fileAccessor = _fileAccessorFactory.Create(workspaceFolder);
        var workflows = cloudFlowMetadata ?? await GetWorkflowsAsync(workspaceFolder, dataverseClient, syncInfo, fileAccessor, cancellationToken).ConfigureAwait(false);
        var aiPrompts = await GetAIPromptsAsync(workspaceFolder, dataverseClient, syncInfo, fileAccessor, cancellationToken).ConfigureAwait(false);
        var changeset = await _islandControlPlaneService.GetComponentsAsync(operationContext, changeToken, cancellationToken).ConfigureAwait(false);

        DefinitionBase emptyDefinition = operationContext switch
        {
            BotComponentCollectionAuthoringOperationContext => new BotComponentCollectionDefinition(flows: workflows.Workflows, connectionReferences: workflows.ConnectionReferences),
            _ => new BotDefinition(flows: workflows.Workflows, connectionReferences: workflows.ConnectionReferences),
        };

        var definition = emptyDefinition.ApplyChanges(changeset);
        if (!aiPrompts.IsDefaultOrEmpty)
        {
            definition = definition.WithAIModelDefinitions(BuildAIModelDefinitions(aiPrompts));
        }

        definition = EnsureEntityCdsBotId(definition, syncInfo.AgentId);
        definition = await ResolveConnectionReferenceConnectorIdsAsync(definition, dataverseClient, cancellationToken).ConfigureAwait(false);

        await fileAccessor.WriteAsync(GitIgnorePath, "*", cancellationToken).ConfigureAwait(false);
        WriteCloudCache(fileAccessor, definition);

        await WriteChangeTokenAsync(fileAccessor, changeset, cancellationToken).ConfigureAwait(false);

        if (updateWorkspaceDirectory)
        {
            await UpdateWorkspaceDirectoryAsync(
                fileAccessor,
                changeset,
                definition,
                deletedComponents: [],
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        await WriteCustomConnectorsAsync(fileAccessor, workspaceFolder, definition, dataverseClient, cancellationToken).ConfigureAwait(false);

        return new WorkspaceSyncInfo
        {
            Definition = definition,
            Changeset = changeset
        };
    }

    private async Task WriteChangeSetAsync(
        DirectoryPath workspaceFolder,
        PvaComponentChangeSet changeset,
        CloudFlowMetadata? cloudFlowMetadata,
        ImmutableArray<AIPromptMetadata> aiPrompts,
        Guid? agentId,
        CancellationToken cancellationToken)
    {
        var fileAccessor = _fileAccessorFactory.Create(workspaceFolder);
        var (definition, deletedComponents) = UpdateCloudCache(fileAccessor, changeset, cloudFlowMetadata, aiPrompts, agentId);
        await UpdateWorkspaceDirectoryAsync(fileAccessor, changeset, definition, deletedComponents, cancellationToken).ConfigureAwait(false);
        await WriteChangeTokenAsync(fileAccessor, changeset, cancellationToken).ConfigureAwait(false);
    }

    // Write the References.mcs.yml - only write a file if there are changes. 
    private Task WriteReferencesAsync(
        IFileAccessor fileAccessor,
        IEnumerable<BotComponentCollectionChange> changes,
        string thisSchema)
    {
        var items = new List<ReferenceItemSourceFile>();
        foreach (var change in changes)
        {
            if (change is BotComponentCollectionUpsert upsert)
            {
                var cc = upsert.ComponentCollection;
                if (cc != null && cc.SchemaName != thisSchema)
                {
                    // Filepath will be filled in in 2nd pass after all components are synced. 
                    var filePath = "";
                    items.Add(new ReferenceItemSourceFile(cc.SchemaName, filePath));
                }
            }
        }

        if (items.Count > 0)
        {
            var refs = new ReferencesSourceFile(items);

            using var file = fileAccessor.OpenWrite(ReferencesCollectionPath);
            using var sw = new StreamWriter(file, Encoding.UTF8);
            CodeSerializer.SerializeWithoutKind(sw, refs);
        }

        return Task.CompletedTask;
    }

    // Write the files in the change set to the user's yaml files.
    // PvaComponentChangeSet only has deleted Ids,
    // so we must pass those components separately. 
    private async Task<DefinitionBase> UpdateWorkspaceDirectoryAsync(
        IFileAccessor fileAccessor,
        PvaComponentChangeSet changeset,
        DefinitionBase definition,
        IReadOnlyList<BotComponentBase> deletedComponents,
        CancellationToken cancellationToken)
    {
        var thisSchema = string.Empty;

        // CliAgentSyncSupport / Nodes D2 + D3 + D4: compute kind ONCE per
        // UpdateWorkspaceDirectoryAsync call and plumb it to every per-
        // component decision (entity write, tool write, skill write,
        // knowledge write, deletes). The shared AgentClassifier is the
        // single classification chokepoint (PRD R1, cli-merge Node F).
        var effectiveEntity = (definition is BotDefinition botForKind)
            ? (changeset.Bot ?? botForKind.Entity)
            : null;
        var isCliAgent = effectiveEntity != null && IsCliAgentEntity(effectiveEntity);

        if (definition is BotComponentCollectionDefinition collection)
        {
            var cc = changeset.ComponentCollectionChanges.OfType<BotComponentCollectionUpsert>().Select(cc => cc.ComponentCollection).FirstOrDefault(static d => d != null);
            WriteComponentCollection(fileAccessor, cc, cancellationToken);

            thisSchema = collection.GetRootSchemaName();
        }
        else if (definition is BotDefinition bot)
        {
            // Prefer changeset.Bot (fresh from cloud) if available, otherwise use bot.Entity from definition.
            await WriteBotEntityAsync(fileAccessor, changeset.Bot ?? bot.Entity, cancellationToken).ConfigureAwait(false);
            thisSchema = bot.GetRootSchemaName();
        }

        // $$$ In the case of Clone, the definition already had ApplyChanges called,
        // and now we're double applying and creating duplicate components.
        // This is benign because the only thing we use these for is looking up ids. 

        var updatedDefinition = definition.ApplyChanges(changeset);
        var writer = new ComponentWriter
        {
            FileAccessor = fileAccessor,
            Definition = updatedDefinition,
            PathResolver = _pathResolver,
            SyncProgress = _syncProgress,
        };

        // Write connectionreferences.mcs.yml with updated connection references from cloud.
        // CliAgentSyncSupport / Node D5: pass isCliAgent so the writer can
        // branch to the per-reference layered shape (infrastructure/connections/)
        // for CLI agents while preserving the classic flat file for everyone
        // else. ConnectionReference is not a BotComponentBase, so this is the
        // only dispatch site (no per-component delete loop participates).
        await WriteConnectionReferencesAsync(fileAccessor, updatedDefinition, isCliAgent, cancellationToken).ConfigureAwait(false);

        // Write References.mcs.yml.
        await WriteReferencesAsync(fileAccessor, changeset.ComponentCollectionChanges, thisSchema).ConfigureAwait(false);

        foreach (var change in changeset.BotComponentChanges)
        {
            if (definition is BotDefinition bot)
            {
                if (change is BotComponentUpsert upsert && upsert.Component is BotComponentBase component)
                {
                    if (component.ParentBotComponentCollectionId.HasValue)
                    {
                        // The component is copied from a component collection.
                        // Don't write it out, instead refer to the source of truth from the component collection.
                        continue;
                    }
                }
            }

            if (IsReusableOrNonCustomizableComponent(change))
            {
                continue;
            }

            change.Accept(writer);
        }

        if (deletedComponents.Count != 0)
        {
            updatedDefinition = updatedDefinition.WithComponents(updatedDefinition.Components.Where(c => !deletedComponents.Any(d => d.SchemaNameString == c.SchemaNameString)));
        }

        foreach (var deleted in deletedComponents)
        {
            // Shape-aware single source of truth (D20/D30): the delete target is
            // the same .mcs.yml path the writer/reader use, so a server-deleted
            // component is removed from the workspace at its projected location.
            var path = new AgentFilePath(_pathResolver.GetComponentPath(deleted, updatedDefinition));
            fileAccessor.Delete(path);
        }

        // Project environment variables as environmentvariables/*.mcs.yml
        WriteEnvironmentVariables(fileAccessor, updatedDefinition, changeset, thisSchema);

        return updatedDefinition;
    }

    private static bool IsReusableOrNonCustomizableComponent(BotComponentChange change)
    {
        if (change is BotComponentUpsert upsert && upsert.Component is BotComponentBase component
            && IsReusableOrNonCustomizableComponent(component))
        {
            // do not write shared component content -- it is not appropriate to emit this to the workspace as it is
            // brought in from an external managed solution.
            return true;
        }

        return false;
    }

    private static bool IsReusableOrNonCustomizableComponent(BotComponentBase component)
    {
        var isReusable = component.ShareContext?.ReusePolicy is BotComponentReusePolicyWrapper reusePolicy
            && !reusePolicy.IsUnknown() && reusePolicy.Value is BotComponentReusePolicy.Private or BotComponentReusePolicy.Public;
        if (isReusable)
        {
            return true;
        }

        if (component.ManagedProperties is ManagedProperties mp && mp.IsManaged && !mp.IsCustomizable)
        {
            return true;
        }

        return false;
    }

    private static void WriteEnvironmentVariables(IFileAccessor fileAccessor, DefinitionBase definition, PvaComponentChangeSet changeset, string agentSchemaName)
    {
        // Process environment variable changes: write upserts, delete removals
        foreach (var change in changeset.EnvironmentVariableChanges)
        {
            if (change is EnvironmentVariableUpsert upsert && upsert.EnvironmentVariable is EnvironmentVariableDefinition envVar)
            {
                WriteEnvironmentVariable(fileAccessor, definition, agentSchemaName, envVar);
            }
            else if (change is EnvironmentVariableDelete delete)
            {
                // Find the env var in the definition to determine its file path for deletion
                var deleted = definition.EnvironmentVariables.FirstOrDefault(e => e.Id.Value == delete.DefinitionId.Value);
                if (deleted != null)
                {
                    var path = GetEnvironmentVariablePath(deleted);
                    fileAccessor.Delete(path);
                }
            }
        }

        // If no individual changes but definition has env vars (e.g., clone), write all
        if (changeset.EnvironmentVariableChanges.IsEmpty)
        {
            foreach (var envVar in definition.EnvironmentVariables)
            {
                WriteEnvironmentVariable(fileAccessor, definition, agentSchemaName, envVar);
            }
        }
    }

    private static void WriteEnvironmentVariable(IFileAccessor fileAccessor, DefinitionBase definition, string agentSchemaName, EnvironmentVariableDefinition? environmentVariableDefinition)
    {
        if (environmentVariableDefinition == null || !environmentVariableDefinition.Id.HasValue)
        {
            return;
        }

        if (GetAgentSchemaName(environmentVariableDefinition.SchemaName.Value) == agentSchemaName && definition.TryGetEnvironmentVariableDefinitionBySchemaName(environmentVariableDefinition.SchemaName, out var environmentVariable) && environmentVariable.Id.HasValue)
        {
            using Stream stream = fileAccessor.OpenWrite(GetEnvironmentVariablePath(environmentVariable));
            using var textWriter = new StreamWriter(stream);
            CodeSerializer.Serialize(textWriter, environmentVariable);
        }
    }

    internal static AgentFilePath GetEnvironmentVariablePath(EnvironmentVariableDefinition envVar)
    {
        var schemaName = envVar.SchemaName.Value;
        return new AgentFilePath($"{EnvironmentVariablesFolder}/{schemaName}.mcs.yml");
    }

    private static string GetAgentSchemaName(string fullSchema) => fullSchema.Substring(0, fullSchema.IndexOf('.') is var i && i > 0 ? i : fullSchema.Length);
    
    private void WriteComponentCollection(IFileAccessor fileAccessor, BotComponentCollection? collection, CancellationToken cancellationToken)
    {
        if (collection == null)
        {
            return;
        }

        using var file = fileAccessor.OpenWrite(ComponentCollectionPath);
        using var sw = new StreamWriter(file, new UTF8Encoding(false));
        CodeSerializer.SerializeWithoutKind(sw, collection.WithOnlyYamlFileProperties());
    }

    private async Task WriteBotEntityAsync(IFileAccessor fileAccessor, BotEntity? entity, CancellationToken cancellationToken)
    {
        if (entity == null)
        {
            return;
        }

        // Icon is a base64 string, but write it as a separate file.
        if (entity.IconBase64 != null)
        {
            var icon = Convert.FromBase64String(entity.IconBase64);
            await fileAccessor.WriteAsync(IconPath, icon, cancellationToken).ConfigureAwait(false);
        }

        // CLI and classic agents both persist the BotEntity identity to the
        // language-recognized settings.mcs.yml via the OM serializer. The CLI
        // recognizer + agentSettings live in BotConfiguration, which
        // WithOnlySettingsYamlProperties preserves, so a single OM path serves both
        // shapes (TDD D22). The separate curated agent.yaml writer is retired.
        using (var file = fileAccessor.OpenWrite(SettingsPath))
        using (var sw = new StreamWriter(file, new UTF8Encoding(false)))
        using (var yamlContext = YamlSerializationContext.UseStandardSerializationContextIfNotDefined(throwOnInvalidYaml: false))
        {
            YamlSerializer.SerializeWithoutKind(sw, entity.WithOnlySettingsYamlProperties());
        }

        // CliAgentSyncSupport / Node Q2 (TDD D29): emit the forward-looking workspace
        // layout marker for CLI agents ONLY. Generic-YAML .sync.yaml (never MCS-parsed),
        // layout-only (no identity/shape echo - settings.mcs.yml is the single identity
        // source). Classic agents emit nothing, preserving classic byte-identity. The
        // marker is excluded from the D30 component allowlist scan by construction (it is
        // .sync.yaml at the root, not a .mcs.yml in a component folder), so push never
        // uploads it.
        if (IsCliAgentEntity(entity))
        {
            await fileAccessor.WriteAsync(AgentSyncMarkerPath, BuildAgentSyncMarker(), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Builds the <c>agent.sync.yaml</c> marker body (TDD D29). Self-describing header
    /// comment + the single <c>layoutVersion</c> field; nothing else.
    /// </summary>
    private static string BuildAgentSyncMarker() =>
        "# Workspace layout marker (Sync overlay; generic YAML, never MCS-parsed).\n" +
        "# Declares the on-disk layout version only; identity lives in settings.mcs.yml.\n" +
        $"layoutVersion: {AgentClassifier.CurrentLayoutVersion}\n";

    /// <summary>
    /// CLI/classic discriminator for the per-component dispatch seam. Routes through
    /// the shared <see cref="AgentClassifier"/> so the sync engine and the product
    /// surfaces use one classification contract (PRD R1). Prefers native CLI
    /// configuration-shape over template prefix (D15).
    /// </summary>
    private static bool IsCliAgentEntity(BotEntity entity)
        => AgentClassifier.DetectAuthoringShape(entity) == AuthoringShape.CliCopilot;

    // This should be bot definition from cloud (not local).
    // This is bot definition at point of time when we synced from cloud.
    // We can use this to Detect local changes and upload back to cloud. 
    internal static void WriteCloudCache(IFileAccessor fileAccessor, DefinitionBase definition)
    {
        using var stream = fileAccessor.OpenWrite(BotCachePath);
        using (YamlSerializationContext.UseYamlPassThroughSerializationContext())
        {
            JsonSerializer.Serialize(stream, definition, ElementSerializer.CreateOptions());
        }
    }

    internal static DefinitionBase? ReadCloudCacheSnapshot(IFileAccessor fileAccessor, bool allowMissing = false)
    {
        if (!fileAccessor.Exists(BotCachePath) && fileAccessor.Exists(OldBotCachePath))
        {
            using var yamlStream = fileAccessor.OpenRead(OldBotCachePath);
            using var reader = new StreamReader(yamlStream, Encoding.UTF8);
            return YamlSerializer.Deserialize<DefinitionBase>(reader);
        }

        if (allowMissing)
        {
            // We can't make any assumptions about file system.
            // File might be deleted, or user could have opened a wrong directory.
            if (!fileAccessor.Exists(BotCachePath))
            {
                return null;
            }
        }

        if (!fileAccessor.Exists(BotCachePath))
        {
            throw new FileNotFoundException($".mcs/botdefinition.json was not found. Please resync.");
        }

        using var stream = fileAccessor.OpenRead(BotCachePath);
        using (YamlSerializationContext.UseYamlPassThroughSerializationContext())
        {
            return JsonSerializer.Deserialize<DefinitionBase>(stream, ElementSerializer.CreateOptions());
        }
    }

    // This will ensure our cloud cache reflects the actual cloud. This is simple because:
    // - the cloud cache has BotIds, so it's easy to apply the changeset.
    // - the user can't edit the cloud cache, so there's never any conflict resolution. 
    private (DefinitionBase newCache, ImmutableArray<BotComponentBase> deletedComponents) UpdateCloudCache(IFileAccessor fileAccessor, PvaComponentChangeSet changeset, CloudFlowMetadata? cloudFlowMetadata = null, ImmutableArray<AIPromptMetadata> aiPrompts = default, Guid? agentId = null)
    {
        var snapshot = ReadCloudCacheSnapshot(fileAccessor);
        if (snapshot == null)
        {
            throw new InvalidOperationException($"Unable to read cloud cache from .mcs/botdefinition.json");
        }

        var deletedComponents = ImmutableArray.CreateBuilder<BotComponentBase>();
        var newSnapshot = snapshot.ApplyChanges(changeset);

        if (newSnapshot is BotDefinition bd && changeset.Bot != null)
        {
            newSnapshot = bd.WithEntity(changeset.Bot);
        }

        newSnapshot = EnsureEntityCdsBotId(newSnapshot, agentId ?? GetSnapshotCdsBotId(snapshot));

        foreach (var item in changeset.BotComponentChanges.OfType<BotComponentDelete>())
        {
            if (snapshot.TryGetBotComponentById(item.BotComponentId, out var component))
            {
                deletedComponents.Add(component);
            }
        }

        if (cloudFlowMetadata != null)
        {
            var mergedConnectionReferences = newSnapshot.ConnectionReferences.Concat(cloudFlowMetadata.ConnectionReferences)
                    .Where(cr => !string.IsNullOrEmpty(cr.ConnectionReferenceLogicalName.Value))
                    .GroupBy(cr => cr.ConnectionReferenceLogicalName.Value, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.Last())
                    .ToList();
            newSnapshot = newSnapshot.WithFlows(cloudFlowMetadata.Workflows).WithConnectionReferences(mergedConnectionReferences);
        }

        if (!aiPrompts.IsDefaultOrEmpty)
        {
            newSnapshot = newSnapshot.WithAIModelDefinitions(BuildAIModelDefinitions(aiPrompts));
        }

        WriteCloudCache(fileAccessor, newSnapshot);
        return (newSnapshot, deletedComponents.ToImmutable());
    }

    private static Guid? GetSnapshotCdsBotId(DefinitionBase snapshot)
    {
        if (snapshot is BotDefinition bd && bd.Entity is BotEntity entity && entity.CdsBotId != default)
        {
            return entity.CdsBotId.Value;
        }

        return null;
    }

    private static DefinitionBase EnsureEntityCdsBotId(DefinitionBase definition, Guid? agentId)
    {
        if (agentId is null || agentId.Value == Guid.Empty)
        {
            return definition;
        }

        if (definition is not BotDefinition bd || bd.Entity is not BotEntity entity || entity.CdsBotId != default)
        {
            return definition;
        }

        var builder = entity.ToBuilder();
        builder.CdsBotId = agentId.Value;
        return bd.WithEntity(builder.Build());
    }

    private async Task WriteChangeTokenAsync(IFileAccessor fileAccessor, PvaComponentChangeSet changeSet, CancellationToken cancellationToken)
    {
        var changeToken = changeSet.ChangeToken;
        if (changeToken == null)
        {
            throw new InvalidOperationException($"Expected change token");
        }

        await fileAccessor.WriteAsync(ChangeTokenPath, changeToken, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> GetChangeTokenOrNullAsync(IFileAccessor fileAccessor, CancellationToken cancellationToken)
    {
        if (!fileAccessor.Exists(ChangeTokenPath))
        {
            return null;
        }

        return await fileAccessor.ReadStringAsync(ChangeTokenPath, cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveSyncInfoAsync(DirectoryPath workspaceFolder, AgentSyncInfo syncInfo)
    {
        var writer = _fileAccessorFactory.Create(workspaceFolder);
        writer.CreateHiddenDirectory(new(HiddenRoot));
        using var stream = writer.OpenWrite(ConnectionDetailsPath);
        await JsonSerializer.SerializeAsync(stream, syncInfo).ConfigureAwait(false);
    }

    /// <summary>
    /// Check if the sync info (mcs\conn.json) is available in the workspace folder.
    /// </summary>
    /// <param name="workspaceFolder">Workspace folder.</param>
    /// <returns>True/False if sync info is available/not available.</returns>
    public bool IsSyncInfoAvailable(DirectoryPath workspaceFolder)
    {
        var fileAccessor = _fileAccessorFactory.Create(workspaceFolder);
        return fileAccessor.Exists(ConnectionDetailsPath);
    }

    public async Task<AgentSyncInfo> GetSyncInfoAsync(DirectoryPath workspaceFolder)
    {
        var accessor = _fileAccessorFactory.Create(workspaceFolder);
        if (!accessor.Exists(ConnectionDetailsPath))
        {
            throw new FileNotFoundException($"The connection file .mcs/conn.json was not found. Please clone the workspace again or perform a reattach.");
        }

        using var stream = accessor.OpenRead(ConnectionDetailsPath);
        var obj = await JsonSerializer.DeserializeAsync<AgentSyncInfo>(stream).ConfigureAwait(false);
        // This only happens if the file contains the string "null".
        if (obj == null)
        {
            throw new InvalidOperationException($"Unable to process content in the connection file .mcs/conn.json.");
        }

        return obj;
    }

    // Determine local changes by comparing the user files to the cloud cache. 
    public async Task<(PvaComponentChangeSet, ImmutableArray<Change>)> GetLocalChangesAsync(DirectoryPath workspaceFolder, DefinitionBase workspaceDefinition, ISyncDataverseClient dataverseClient, AgentSyncInfo syncInfo, CancellationToken cancellationToken)
    {
        var fileAccessor = _fileAccessorFactory.Create(workspaceFolder);
        var cloudSnapshot = ReadCloudCacheSnapshot(fileAccessor);
        if (cloudSnapshot == null)
        {
            throw new InvalidOperationException($"Unable to read cloud cache from .mcs/botdefinition.json");
        }

        var changeToken = await GetChangeTokenOrNullAsync(fileAccessor, cancellationToken).ConfigureAwait(false);
        var effectiveDefinition = OverlayCliConnectionReferences(workspaceDefinition, fileAccessor, cancellationToken);
        var (changeSet, changes) = GetLocalChanges(effectiveDefinition, cloudSnapshot, fileAccessor, changeToken, isRemoteChange: false, deferMissingParents: true, out _);

        var workflowChanges = GetLocalWorkflowChangesAsync(workspaceFolder, dataverseClient, syncInfo, fileAccessor, cloudSnapshot, cancellationToken);
        changes = changes.AddRange(await workflowChanges.ConfigureAwait(false));

        return (changeSet, changes);
    }

    /// <summary>
    /// Overlays the on-disk CLI connection references (the per-reference
    /// <c>infrastructure/connections/*.sync.yaml</c> layered shape) onto a definition before a
    /// local diff (TDD D38). CLI connection-reference files route to generic YAML and never
    /// reach the MCS workspace compiler, so the LSP <c>workspace.Definition</c> the push /
    /// local-diff handlers pass into <see cref="GetLocalChangesAsync"/> does NOT contain them.
    /// Without this overlay <see cref="GetCliConnectionReferenceChanges"/> builds its
    /// insert/update set from a definition missing the disk references, so CLI
    /// connection-reference CREATE/UPDATE are missed on the VS Code push/diff path (the delete
    /// side already enumerates disk via <c>ListDiskLogicalNames</c>). This mirrors the overlay
    /// <see cref="ReadWorkspaceDefinitionAsync"/> applies on the sync-engine path, and is a
    /// no-op for classic agents and for an already-overlaid definition.
    /// </summary>
    private DefinitionBase OverlayCliConnectionReferences(DefinitionBase definition, IFileAccessor fileAccessor, CancellationToken cancellationToken)
    {
        var entity = (definition as BotDefinition)?.Entity;
        if (entity != null && IsCliAgentEntity(entity) && CliAgentConnectionsReader.IsLayeredShapeActive(fileAccessor))
        {
            var overlaid = CliAgentConnectionsReader.Overlay(
                fileAccessor, definition.ConnectionReferences, _syncProgress.Report, cancellationToken);
            return definition.WithConnectionReferences(overlaid.ToImmutableArray());
        }

        return definition;
    }

    // Determine remote changes by comparing the user files to the cloud cache. 
    public async Task<(PvaComponentChangeSet, ImmutableArray<Change>)> GetRemoteChangesAsync(DirectoryPath workspaceFolder, AuthoringOperationContextBase operationContext, ISyncDataverseClient dataverseClient, AgentSyncInfo syncInfo, CancellationToken cancellationToken)
    {
        var fileAccessor = _fileAccessorFactory.Create(workspaceFolder);
        var changeToken = await GetChangeTokenOrNullAsync(fileAccessor, cancellationToken).ConfigureAwait(false);
        var changeset = await _islandControlPlaneService.GetComponentsAsync(
                operationContext,
                changeToken: changeToken,
                cancellationToken).ConfigureAwait(false);

        var cloudSnapshot = ReadCloudCacheSnapshot(fileAccessor) ?? BotDefinition.Empty;

        var appliedDefinition = cloudSnapshot.ApplyChanges(changeset);
        var computeChanges = GetLocalChanges(appliedDefinition, cloudSnapshot, fileAccessor, changeToken, isRemoteChange: true);

        var changes = computeChanges.Item2;
        var workflowChanges = GetRemoteWorkflowChangesAsync(dataverseClient, syncInfo, cloudSnapshot, cancellationToken);
        changes = changes.AddRange(await workflowChanges.ConfigureAwait(false));

        return (changeset, changes);
    }

    public (PvaComponentChangeSet, ImmutableArray<Change>) GetLocalChanges(DefinitionBase localDefinition, DefinitionBase cloudSnapshot, IFileAccessor fileAccessor, string? changeToken, bool isRemoteChange = false)
        => GetLocalChanges(localDefinition, cloudSnapshot, fileAccessor, changeToken, isRemoteChange, deferMissingParents: false, out _);

    public (PvaComponentChangeSet, ImmutableArray<Change>) GetLocalChanges(DefinitionBase localDefinition, DefinitionBase cloudSnapshot, IFileAccessor fileAccessor, string? changeToken, bool isRemoteChange, bool deferMissingParents, out bool deferredMissingParent)
    {
        deferredMissingParent = false;
        var changes = ImmutableArray.CreateBuilder<Change>();
        var botComponentBuilderList = new List<BotComponentChange>();

        BotEntity? botEntity = null;
        BotEntityId parentBotId = default;
        BotComponentCollectionId parentCollectionId = default;
        if (localDefinition is BotComponentCollectionDefinition collection)
        {
            parentCollectionId = collection.ComponentCollection?.Id ?? throw new InvalidOperationException("Missing component collection definition");
        }
        else if (localDefinition is BotDefinition bot)
        {
            botEntity = bot.Entity ?? throw new InvalidOperationException("Missing bot definition");
            parentBotId = botEntity.CdsBotId;
            var cloudSnapshotEntity = (cloudSnapshot as BotDefinition)?.Entity;
            if (cloudSnapshotEntity != null)
            {
                var leftComparison = cloudSnapshotEntity.WithOnlySettingsYamlProperties();
                var rightComparison = botEntity.WithOnlySettingsYamlProperties();

                // only generate changes if the content in Settings.mcs.yml has changed
                // ignore syntax differences
                if (!leftComparison.Equals(rightComparison, NodeComparison.Structural))
                {
                    var settingsPathValue = SettingsPath.ToString();
                    var change = new Change
                    {
                        ChangeType = ChangeType.Update,
                        Name = "Settings",
                        Uri = settingsPathValue,
                        SchemaName = "entity",
                        ChangeKind = botEntity.Kind.ToString()
                    };

                    if (cloudSnapshotEntity is null)
                    {
                        changes.Add(change with { ChangeType = ChangeType.Create });
                    }
                    if (cloudSnapshotEntity == null || !botEntity.Equals(cloudSnapshotEntity, NodeComparison.Structural))
                    {
                        changes.Add(change with { ChangeType = ChangeType.Update });
                    }
                }
            }

            // If icon has changed, highlight it.
            if (cloudSnapshotEntity != null && cloudSnapshotEntity.IconBase64 != botEntity.IconBase64)
            {
                var iconPathValue = IconPath.ToString();
                var change = new Change
                {
                    ChangeType = ChangeType.Update,
                    Name = "Icon",
                    Uri = iconPathValue,
                    SchemaName = "icon",
                    ChangeKind = "update"
                };

                changes.Add(change with { ChangeType = ChangeType.Update });
            }
        }

        // incoming newBotDefinition does not have ids, versions numbers.
        // We must correlate to the cloud snapshot to get those. 

        // Inserts, Updates, Deletes.
        foreach (var localComponent in localDefinition.Components)
        {
            if (IsReusableOrNonCustomizableComponent(localComponent))
            {
                continue;
            }

            BotComponentId parentBotComponentId = default;
            // Remap local botIds (which were fabricated) to real botIds from the cloud.
            if (localComponent.ParentBotComponentId.HasValue && localComponent is not FileAttachmentComponent)
            {
                var localParentComponent = localDefinition.VerifiedGetBotComponentById(localComponent.ParentBotComponentId);
                var parentSchemaName = localParentComponent.SchemaNameString;
                if (cloudSnapshot.TryGetComponentBySchemaName(parentSchemaName, out var cloudComponentParent))
                {
                    parentBotComponentId = cloudComponentParent.Id;
                }
                else if (isRemoteChange)
                {
                    parentBotComponentId = localParentComponent.Id;
                }
                else if (deferMissingParents)
                {
                    deferredMissingParent = true;
                    continue;
                }
                else
                {
                    // This happens if we locally create both a new sub agent, and new component in that new agent.
                    // Instead, we need to do this in 2 passes:
                    // - first upload the new sub agents (to create the component ids)
                    // - then upload the new child components.
                    throw new InvalidOperationException($"Unsupported sync operation. ParentId does not exist on cloud: {parentSchemaName}");
                }
            }

            if (cloudSnapshot.TryGetComponentBySchemaName(localComponent.SchemaNameString, out var cloudComponent))
            {
                // In new and old ... --> possible Update
                var r1 = localComponent.RootElement == null ? null : StripMetaInfo(localComponent.RootElement);
                var r2 = cloudComponent.RootElement == null ? null : StripMetaInfo(cloudComponent.RootElement);
                var same = (string.IsNullOrWhiteSpace(localComponent.DisplayName) || NormalizeString(localComponent.DisplayName) == NormalizeString(cloudComponent.DisplayName)) &&
                                    (string.IsNullOrWhiteSpace(localComponent.Description) || NormalizeString(localComponent.Description) == NormalizeString(cloudComponent.Description));

                if (localComponent is not FileAttachmentComponent)
                {
                    same = same && (r1 is not null) && (r2 is not null) && r1.Equals(r2, NodeComparison.Structural);
                }

                if (!same)
                {
                    var b2 = localComponent.ToBuilder();
                    b2.Id = cloudComponent.Id;
                    b2.Version = cloudComponent.Version;
                    b2.ParentBotId = parentBotId;
                    b2.ParentBotComponentId = parentBotComponentId;
                    b2.ParentBotComponentCollectionId = parentCollectionId;
                    b2.DisplayName = localComponent.DisplayName;
                    b2.Description = localComponent.Description;
                    botComponentBuilderList.Add(new BotComponentUpdate(b2.Build()));
                    changes.Add(new Change() { ChangeType = ChangeType.Update, Name = b2.SchemaNameString, Uri = _pathResolver.GetComponentPath(localComponent, localDefinition), SchemaName = cloudComponent.SchemaNameString, ChangeKind = cloudComponent.Kind.ToString() });
                }
            }
            else
            {
                // In local, but not in cloud . --> Insert to cloud
                var b2 = localComponent.ToBuilder();
                b2.ParentBotId = parentBotId;
                b2.ParentBotComponentId = parentBotComponentId;
                b2.ParentBotComponentCollectionId = parentCollectionId;
                b2.DisplayName = localComponent.DisplayName;
                b2.Description = localComponent.Description;
                botComponentBuilderList.Add(new BotComponentInsert(b2.Build()));
                changes.Add(new Change() { ChangeType = ChangeType.Create, Name = b2.SchemaNameString, Uri = _pathResolver.GetComponentPath(localComponent, localDefinition), SchemaName = b2.SchemaNameString, ChangeKind = localComponent.Kind.ToString() });
            }
        }

        foreach (var cloudComponent in cloudSnapshot.Components)
        {
            if (!IsReusableOrNonCustomizableComponent(cloudComponent) && !localDefinition.TryGetComponentBySchemaName(cloudComponent.SchemaNameString, out var _))
            {
                if (cloudComponent is FileAttachmentComponent)
                {
                    continue;
                }

                botComponentBuilderList.Add(new BotComponentDelete(cloudComponent.Id, cloudComponent.Version));
                changes.Add(new Change() { ChangeType = ChangeType.Delete, Name = cloudComponent.SchemaNameString, Uri = _pathResolver.GetComponentPath(cloudComponent, cloudSnapshot), SchemaName = cloudComponent.SchemaNameString, ChangeKind = cloudComponent.Kind.ToString() });
            }
        }

        // Handle EnvironmentVariableDefinitions
        var (environmentVariableChanges, changesOfEnvironmentVariables) = GetEnvironmentVariableLocalChanges(localDefinition, cloudSnapshot, fileAccessor, isRemoteChange);
        changes.AddRange(changesOfEnvironmentVariables);

        // CliAgentSyncSupport / Node F: connection-reference change
        // emission. CLI-only; classic agents have no per-reference disk
        // shape so there's nothing to diff.
        List<ConnectionReferenceChange>? connectionReferenceChanges = null;
        if (botEntity != null && IsCliAgentEntity(botEntity))
        {
            connectionReferenceChanges = GetCliConnectionReferenceChanges(
                fileAccessor, localDefinition, cloudSnapshot, changes, isRemoteChange);
        }

        var changeset = new PvaComponentChangeSet(
              botComponentChanges: botComponentBuilderList,
              connectorDefinitionChanges: null,
              environmentVariableChanges: environmentVariableChanges,
              connectionReferenceChanges: connectionReferenceChanges,
              aIPluginOperationChanges: null,
              componentCollectionChanges: null,
              dataverseTableSearchChanges: null,
              dataverseTableSearchEntityConfigurationChanges: null,
              connectedAgentDefinitionChanges: null,
              bot: botEntity,
              changeToken: changeToken);

        return (changeset, changes.ToImmutable());
    }

    /// <summary>
    /// CliAgentSyncSupport / Node F: diff connection references against the
    /// cloud-cache snapshot and emit Insert/Update/Delete changes. CLI-only.
    /// </summary>
    /// <remarks>
    /// Delete detection (TDD D32) sources its "present" set by direction:
    /// <list type="bullet">
    /// <item>LOCAL (push) diff (<paramref name="isRemoteChange"/> false):
    /// <paramref name="localDefinition"/> is the post-overlay disk read, and Node E's
    /// overlay preserves cloud-only refs verbatim, so "deleted on disk" never shows up as
    /// missing from the in-memory definition. The disk enumeration
    /// (<see cref="CliAgentConnectionsReader.ListDiskLogicalNames"/>) is the only signal
    /// that surfaces destructive intent, and deletes are gated on isCliLayoutAdopted so a
    /// pre-D1 clone never synthesizes phantom delete intent.</item>
    /// <item>REMOTE (pull) preview (<paramref name="isRemoteChange"/> true):
    /// <paramref name="localDefinition"/> is the cloud-applied snapshot (the NEW cloud
    /// state), so its own ref set is authoritative and local disk must NOT be consulted
    /// (mirrors the env-var handling in <see cref="GetEnvironmentVariableLocalChanges"/>):
    /// a ref in the old cloud cache but absent from the new cloud state is an incoming
    /// remote delete, while a local-only on-disk delete is NOT an incoming remote change.</item>
    /// </list>
    /// </remarks>
    private List<ConnectionReferenceChange> GetCliConnectionReferenceChanges(
        IFileAccessor fileAccessor,
        DefinitionBase localDefinition,
        DefinitionBase cloudSnapshot,
        ImmutableArray<Change>.Builder changes,
        bool isRemoteChange = false)
    {
        var result = new List<ConnectionReferenceChange>();
        var isCliLayoutAdopted = CliAgentBotEntityReader.IsCliLayoutAdopted(fileAccessor);

        // Index cloud refs by logical name (OrdinalIgnoreCase — matches
        // CliAgentConnectionsReader / WriteConnectionReferencesAsync).
        var cloudByName = new Dictionary<string, ConnectionReference>(StringComparer.OrdinalIgnoreCase);
        foreach (var cr in cloudSnapshot.ConnectionReferences)
        {
            var name = cr?.ConnectionReferenceLogicalName.Value;
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }
            cloudByName[name!] = cr!;
        }

        // Local definition is post-overlay (Node E); reflects disk content
        // plus cloud-only refs. Drive Insert/Update from this set.
        var localByName = new Dictionary<string, ConnectionReference>(StringComparer.OrdinalIgnoreCase);
        foreach (var cr in localDefinition.ConnectionReferences)
        {
            var name = cr?.ConnectionReferenceLogicalName.Value;
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }
            localByName[name!] = cr!;
        }

        // Present-name set drives delete detection (a cloud ref absent from this set is
        // treated as deleted). Sourced by direction per TDD D32 - see the method remarks.
        HashSet<string> presentNames;
        bool emitDeletes;
        if (isRemoteChange)
        {
            presentNames = new HashSet<string>(localByName.Keys, StringComparer.OrdinalIgnoreCase);
            emitDeletes = true;
        }
        else
        {
            presentNames = isCliLayoutAdopted
                ? CliAgentConnectionsReader.ListDiskLogicalNames(fileAccessor)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            emitDeletes = isCliLayoutAdopted;
        }

        foreach (var kvp in localByName)
        {
            var name = kvp.Key;
            var local = kvp.Value;

            if (cloudByName.TryGetValue(name, out var cloud))
            {
                var connectorChanged = !string.Equals(
                    local.ConnectorId.Value ?? string.Empty,
                    cloud.ConnectorId.Value ?? string.Empty,
                    StringComparison.Ordinal);
                if (connectorChanged)
                {
                    // Carry forward cloud Id/Version on Update so the
                    // server can apply by Id.
                    var builder = local.ToBuilder();
                    builder.Id = cloud.Id;
                    builder.Version = cloud.Version;
                    result.Add(new ConnectionReferenceUpdate(builder.Build()));
                    changes.Add(new Change
                    {
                        ChangeType = ChangeType.Update,
                        Name = name,
                        Uri = $"{CliAgentConnectionsWriter.InfrastructureConnectionsFolder}/{name}{CliAgentConnectionsWriter.FileExtension}",
                        SchemaName = name,
                        ChangeKind = nameof(ConnectionReference),
                    });
                }
            }
            else
            {
                result.Add(new ConnectionReferenceInsert(local));
                changes.Add(new Change
                {
                    ChangeType = ChangeType.Create,
                    Name = name,
                    Uri = $"{CliAgentConnectionsWriter.InfrastructureConnectionsFolder}/{name}{CliAgentConnectionsWriter.FileExtension}",
                    SchemaName = name,
                    ChangeKind = nameof(ConnectionReference),
                });
            }
        }

        if (!emitDeletes)
        {
            // Pre-D1 local clone (no CLI layout adopted) — never emit destructive delete
            // intent. Remote previews always emit deletes (emitDeletes is forced true).
            return result;
        }

        foreach (var kvp in cloudByName)
        {
            var name = kvp.Key;
            var cloud = kvp.Value;
            if (presentNames.Contains(name))
            {
                continue;
            }
            if (cloud.Id.Value == Guid.Empty)
            {
                // Cloud entry without a server-assigned Id can't be deleted
                // through the changeset (Apply pivots on Id). Skip and log.
                _syncProgress.Report(
                    $"CLI connection reference '{name}' missing from disk but cloud cache has no Id; cannot emit delete.");
                continue;
            }
            result.Add(new ConnectionReferenceDelete(cloud.Id.Value, cloud.Version));
            changes.Add(new Change
            {
                ChangeType = ChangeType.Delete,
                Name = name,
                Uri = $"{CliAgentConnectionsWriter.InfrastructureConnectionsFolder}/{name}{CliAgentConnectionsWriter.FileExtension}",
                SchemaName = name,
                ChangeKind = nameof(ConnectionReference),
            });
        }

        return result;
    }

    private static (ImmutableArray<EnvironmentVariableChange>, ImmutableArray<Change>) GetEnvironmentVariableLocalChanges(DefinitionBase localDefinition, DefinitionBase cloudSnapshot, IFileAccessor fileAccessor, bool isRemoteChange = false)
    {
        var changes = ImmutableArray.CreateBuilder<Change>();
        var environmentVariableChanges = new List<EnvironmentVariableChange>();
        var localEnvironmentVariables = localDefinition.EnvironmentVariables.Where(ev => ev != null && GetAgentSchemaName(ev.SchemaName.Value) == localDefinition.GetRootSchemaName()).Where(ev => isRemoteChange || fileAccessor.Exists(GetEnvironmentVariablePath(ev)));
        var cloudEnvironmentVariables = cloudSnapshot.EnvironmentVariables.Where(ev => ev != null && GetAgentSchemaName(ev.SchemaName.Value) == cloudSnapshot.GetRootSchemaName());
        var localEnvironmentVariableDictionary = localEnvironmentVariables.ToDictionary(ev => ev.SchemaName, ev => ev);
        var cloudEnvironmentVariableDictionary = cloudEnvironmentVariables.ToDictionary(ev => ev.SchemaName, ev => ev);

        foreach (var localEnvironmentVariable in localEnvironmentVariables)
        {
            if (localEnvironmentVariable != null)
            {
                if (cloudEnvironmentVariableDictionary.TryGetValue(localEnvironmentVariable.SchemaName, out var cloudEnvironmentVariable))
                {
                    var same = localEnvironmentVariable.Equals(cloudEnvironmentVariable, NodeComparison.Structural);

                    if (!same)
                    {
                        environmentVariableChanges.Add(new EnvironmentVariableUpdate(localEnvironmentVariable));
                        changes.Add(new Change
                        {
                            ChangeType = ChangeType.Update,
                            Name = localEnvironmentVariable.DisplayName ?? localEnvironmentVariable.SchemaName.Value,
                            Uri = EnvironmentVariablesFolder + $"/{localEnvironmentVariable.SchemaName.Value}.mcs.yml",
                            SchemaName = localEnvironmentVariable.SchemaName.Value,
                            ChangeKind = BotElementKind.EnvironmentVariableDefinition.ToString()
                        });
                    }
                }
                else
                {
                    environmentVariableChanges.Add(new EnvironmentVariableInsert(localEnvironmentVariable));
                    changes.Add(new Change
                    {
                        ChangeType = ChangeType.Create,
                        Name = localEnvironmentVariable.DisplayName ?? localEnvironmentVariable.SchemaName.Value,
                        Uri = EnvironmentVariablesFolder + $"/{localEnvironmentVariable.SchemaName.Value}.mcs.yml",
                        SchemaName = localEnvironmentVariable.SchemaName.Value,
                        ChangeKind = BotElementKind.EnvironmentVariableDefinition.ToString()
                    });
                }
            }
        }

        foreach (var cloudEnvironmentVariable in cloudEnvironmentVariables)
        {
            if (cloudEnvironmentVariable.ValueComponent != null && !localEnvironmentVariableDictionary.ContainsKey(cloudEnvironmentVariable.SchemaName))
            {
                environmentVariableChanges.Add(new EnvironmentVariableDelete(
                    cloudEnvironmentVariable.Id,
                    cloudEnvironmentVariable.ValueComponent.Id,
                    cloudEnvironmentVariable.Version
                ));

                changes.Add(new Change
                {
                    ChangeType = ChangeType.Delete,
                    Name = cloudEnvironmentVariable.DisplayName ?? cloudEnvironmentVariable.SchemaName.Value,
                    Uri = EnvironmentVariablesFolder + $"/{cloudEnvironmentVariable.SchemaName.Value}.mcs.yml",
                    SchemaName = cloudEnvironmentVariable.SchemaName.Value,
                    ChangeKind = BotElementKind.EnvironmentVariableDefinition.ToString()
                });
            }
        }

        return (environmentVariableChanges.ToImmutableArray(), changes.ToImmutable());
    }

    private static string NormalizeString(string? value) => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().Trim('"').Replace("\r\n", "\n").Replace("\r", "\n");

    private static BotElement StripMetaInfo(BotElement botElement)
    {
        if (botElement.ExtensionData is not RecordDataValue record || !record.Properties.ContainsKey("mcs.metadata"))
        {
            return botElement;
        }

        var stripedMetaRecord = record.Properties.Remove("mcs.metadata");
        return botElement.WithExtensionData(stripedMetaRecord.IsEmpty ? null : new RecordDataValue(stripedMetaRecord));
    }

    private string? GetMcsYaml(BotElement? element)
    {
        if (element == null)
        {
            return null;
        }

        using var sw = new StringWriter();
        if (element is BotComponentBase component)
        {
            if (component.RootElement != null)
            {
                CodeSerializer.Serialize(sw, component.RootElement);
            }
        }
        else
        {
            CodeSerializer.Serialize(sw, element);
        }

        return sw.ToString();
    }

    private class ComponentWriter : BotComponentChangeVisitor
    {
        public IFileAccessor FileAccessor { get; init; } = null!;

        public DefinitionBase Definition { get; init; } = null!;

        public IComponentPathResolver PathResolver { get; init; } = null!;

        public ISyncProgress? SyncProgress { get; init; }

        public override void Visit(UnknownBotComponentChange item)
        {
            // ignore unknown changes
        }

        public override void Visit(BotComponentInsert item)
        {
            Write(item.Component);
        }

        public override void Visit(BotComponentUpdate item)
        {
            Write(item.Component);
        }

        public override void Visit(BotComponentDelete item)
        {
            // handle delete separately
        }

        // Write (for insert/update).
        private void Write(BotComponentBase? botComponent)
        {
            if (botComponent == null)
            {
                return;
            }

            if (!botComponent.Id.HasValue)
            {
                // Fail now, because this would cause VerifiedGetBotComponentById
                // to return the wrong component!
                throw new InvalidOperationException($"Component is missing Id: {botComponent.SchemaNameString}");
            }

            // Bot definitions from the PvaChangeSet are orphaned and not in a BotDefinition.
            // Need the BotDef to do things like compute file path.
            // Use TryGet instead of VerifiedGet to avoid crash when a component type
            // (e.g., SkillComponent in batch push) isn't properly round-tripped through
            // ApplyChanges or deserialization.
            if (!Definition.TryGetBotComponentById(botComponent.Id, out var groundedComponent))
            {
                SyncProgress?.Report($"Skipping component {botComponent.SchemaNameString}: not found in definition after ApplyChanges (Id: {botComponent.Id})");
                return;
            }

            // CLI and classic both fall through to the single tail: the
            // shape-aware PathResolver.GetComponentPath (TDD D20/D30 derives the
            // authoring shape from Definition.Entity) routes CLI components to
            // the three-layer .mcs.yml paths and classic components to their
            // classic locations, and SerializeAsMcsYml emits the full component
            // body (with mcs.metadata) at that path. The hand-coded CliAgent*
            // writer dispatch (bare-dialog .yaml bodies) is retired (Node Q).
            var path = new AgentFilePath(PathResolver.GetComponentPath(groundedComponent, Definition));
            using var stream = FileAccessor.OpenWrite(path);
            using var textWriter = new StreamWriter(stream);
            CodeSerializer.SerializeAsMcsYml(textWriter, NormalizeForMcsYml(groundedComponent));
        }

        private static BotComponentBase NormalizeForMcsYml(BotComponentBase component)
        {
            if (component is not DialogComponent dialogComponent || dialogComponent.Dialog is not InlineAgentSkill)
            {
                return component;
            }

            if (string.IsNullOrWhiteSpace(component.DisplayName) && string.IsNullOrWhiteSpace(component.Description))
            {
                return component;
            }

            using var probe = new StringWriter();
            CodeSerializer.SerializeAsMcsYml(probe, component);
            if (probe.ToString().Contains("mcs.metadata:", StringComparison.Ordinal))
            {
                return component;
            }

            try
            {
                string json;
                using (YamlSerializationContext.UseYamlPassThroughSerializationContext())
                {
                    json = JsonSerializer.Serialize<DefinitionBase>(new BotDefinition().WithComponents(ImmutableArray.Create((BotComponentBase)component)), ElementSerializer.CreateOptions());
                }

                DefinitionBase? roundTripped;
                using (YamlSerializationContext.UseYamlPassThroughSerializationContext())
                {
                    roundTripped = JsonSerializer.Deserialize<DefinitionBase>(json, ElementSerializer.CreateOptions());
                }

                return roundTripped?.Components.SingleOrDefault() ?? component;
            }
            catch (JsonException)
            {
                return component;
            }
        }
    }

    public virtual async Task<(ImmutableArray<WorkflowResponse>, CloudFlowMetadata)> UpsertWorkflowForAgentAsync(DirectoryPath workspaceFolder, ISyncDataverseClient dataverseClient, Guid? agentId, CancellationToken cancellationToken)
    {
        var cloudFlowDefinitions = new List<CloudFlowDefinition>();
        var workflows = new List<WorkflowMetadata>();
        var connectionReferences = ImmutableArray<ConnectionReference>.Empty;
        var workflowResponseBuilder = ImmutableArray.CreateBuilder<WorkflowResponse>();

        var fileAccessor = _fileAccessorFactory.Create(workspaceFolder);
        var cloudSnapshot = ReadCloudCacheSnapshot(fileAccessor, allowMissing: true);
        var cachedWorkflowClientData = new Dictionary<Guid, string>();
        var cachedWorkflowMetadata = new Dictionary<Guid, string>();
        if (cloudSnapshot?.Flows != null)
        {
            foreach (var flow in cloudSnapshot.Flows)
            {
                cachedWorkflowClientData[flow.WorkflowId.Value] = GetClientData(flow);
                cachedWorkflowMetadata[flow.WorkflowId.Value] = GetWorkflowMetadata(flow);
            }
        }

        var workflowsDir = Path.Combine(workspaceFolder.ToString(), WorkflowFolder);
        if (Directory.Exists(workflowsDir))
        {
            var deserializer = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).Build();
            var workflowsToUpload = new List<WorkflowMetadata>();

            foreach (var workflowFolder in Directory.EnumerateDirectories(workflowsDir))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var workflowName = Path.GetFileName(workflowFolder);
                var workflowId = ExtractWorkflowIdFromFileName(workflowName);

                if (workflowId == null)
                {
                    continue;
                }

                var jsonFile = Path.Combine(workflowFolder, "workflow.json");
                var metadataFile = Path.Combine(workflowFolder, "metadata.yml");
                if (!File.Exists(jsonFile) || !File.Exists(metadataFile))
                {
                    continue;
                }

                var workflowUploadSize = new FileInfo(jsonFile).Length + new FileInfo(metadataFile).Length;
                if (workflowUploadSize > MaxWorkflowUploadSizeBytes)
                {
                    _syncProgress.Report($"Workflow '{workflowName}' exceeded the upload size limit of 125MB and will be skipped.");
                    continue;
                }

                var clientDataJson = await FileShim.ReadAllTextAsync(jsonFile, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
                var yamlText = await FileShim.ReadAllTextAsync(metadataFile, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
                var metadata = deserializer.Deserialize<WorkflowMetadata>(yamlText)
                    ?? throw new InvalidOperationException($"Workflow metadata file is empty or invalid.");
                metadata.ClientData = clientDataJson;
                workflows.Add(metadata);

                var (cloudFlowDefinition, _) = GetFlowDefinition(metadata);
                cloudFlowDefinitions.Add(cloudFlowDefinition);

                if (cachedWorkflowClientData.TryGetValue(workflowId.Value, out var cachedClientData)
                    && string.Equals(cachedClientData, NormalizeWorkflowClientData(clientDataJson), StringComparison.Ordinal)
                    && cachedWorkflowMetadata.TryGetValue(workflowId.Value, out var cachedMetadata)
                    && string.Equals(cachedMetadata, NormalizeWorkflowMetadata(metadata), StringComparison.Ordinal))
                {
                    continue;
                }

                workflowsToUpload.Add(metadata);
            }

            if (workflowsToUpload.Count > 0)
            {
#if NETSTANDARD2_0
                foreach (var metadata in workflowsToUpload)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var workflowResponse = await dataverseClient.UpdateWorkflowAsync(agentId, metadata, cancellationToken).ConfigureAwait(false);
                    workflowResponseBuilder.Add(workflowResponse);
                }
#else
                var workflowResponses = new ConcurrentBag<WorkflowResponse>();
                await Parallel.ForEachAsync(workflowsToUpload, new ParallelOptions
                {
                    MaxDegreeOfParallelism = 5,
                    CancellationToken = cancellationToken
                },
                async (metadata, ct) =>
                {
                    var workflowResponse = await dataverseClient.UpdateWorkflowAsync(agentId, metadata, ct).ConfigureAwait(false);
                    workflowResponses.Add(workflowResponse);
                }).ConfigureAwait(false);
                workflowResponseBuilder.AddRange(workflowResponses);
#endif
            }

            connectionReferences = await GetConnectionReferenceFromLogicalNamesAsync(GetConnectionReferenceLogicalNamesFromFlows(workflows), dataverseClient, cancellationToken).ConfigureAwait(false);
        }

        return (workflowResponseBuilder.ToImmutable(), new CloudFlowMetadata
        {
            Workflows = cloudFlowDefinitions.ToImmutableArray(),
            ConnectionReferences = connectionReferences
        });
    }

    public async Task<CloudFlowMetadata> GetWorkflowsAsync(DirectoryPath workspaceFolder, ISyncDataverseClient dataverseClient, AgentSyncInfo syncInfo, IFileAccessor fileAccessor, CancellationToken cancellationToken)
    {
        var cloudFlowDefinitions = new List<CloudFlowDefinition>();
        var workflows = new List<WorkflowMetadata>();
        var connectionReferences = ImmutableArray<ConnectionReference>.Empty;

        try
        {
            var remote = await dataverseClient.DownloadAllWorkflowsForAgentAsync(syncInfo, cancellationToken).ConfigureAwait(false);
            var workflowsRoot = Path.Combine(workspaceFolder.ToString(), WorkflowFolder);

            Directory.CreateDirectory(workflowsRoot);
            var existingFolders = new Dictionary<Guid, string>();

            foreach (var folder in Directory.EnumerateDirectories(workflowsRoot))
            {
                var workflowId = ExtractWorkflowIdFromFileName(Path.GetFileName(folder));
                if (workflowId.HasValue)
                {
                    existingFolders[workflowId.Value] = folder;
                }
            }

            if (remote is null || remote.Length == 0)
            {
                foreach (var folder in existingFolders.Values)
                {
                    if (Directory.Exists(folder))
                    {
                        Directory.Delete(folder, true);
                    }
                }

                return new CloudFlowMetadata
                {
                    Workflows = ImmutableArray<CloudFlowDefinition>.Empty,
                    ConnectionReferences = ImmutableArray<ConnectionReference>.Empty
                };
            }

            foreach (var workflow in remote)
            {
                cancellationToken.ThrowIfCancellationRequested();

                workflows.Add(workflow);

                var (definition, _) = GetFlowDefinition(workflow);
                cloudFlowDefinitions.Add(definition);
                var folderName = $"{new string(((workflow.Name ?? string.Empty)).Where(c => !Path.GetInvalidFileNameChars().Contains(c) && !char.IsWhiteSpace(c)).ToArray()).TrimEnd('.', ' ')}-{workflow.WorkflowId}";
                var folderPath = Path.Combine(workflowsRoot, folderName);

                if (existingFolders.TryGetValue(workflow.WorkflowId, out var existingFolderPath))
                {
                    if (!string.Equals(existingFolderPath, folderPath, StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            Directory.Move(existingFolderPath, folderPath);
                        }
                        catch (IOException) when (Directory.Exists(folderPath))
                        {
                            Directory.Delete(folderPath, true);
                            Directory.Move(existingFolderPath, folderPath);
                        }
                    }
                    existingFolders[workflow.WorkflowId] = folderPath;
                }
                else
                {
                    Directory.CreateDirectory(folderPath);
                    existingFolders[workflow.WorkflowId] = folderPath;
                }

                if (!string.IsNullOrWhiteSpace(workflow.ClientData))
                {
                    var workflowFolder = Path.Combine(WorkflowFolder, folderName).Replace("\\", "/");
                    var workflowJson = new AgentFilePath($"{workflowFolder}/workflow.json");
                    var workflowJsonTmp = new AgentFilePath($"{workflowFolder}/workflow.json.tmp");
                    workflow.JsonFileName = workflowJson.ToString();

                    // ns2.0 BCL's IsNullOrWhiteSpace lacks NotNullWhen; ! is compile-time only.
                    using var jsonDoc = JsonDocument.Parse(workflow.ClientData!);
                    var jsonString = JsonSerializer.Serialize(jsonDoc.RootElement, new JsonSerializerOptions { WriteIndented = true });

                    if (await WorkflowFileNeedsWriteAsync(fileAccessor, workflowJson, jsonString, cancellationToken).ConfigureAwait(false))
                    {
                        // net10 uses async disposal so the StreamWriter and FileStream flush
                        // asynchronously; netstandard2.0's Stream is not IAsyncDisposable, so
                        // it falls back to sync using. The sync-flush cost on the ns2.0 path
                        // is bounded (workflow JSON payloads are 1-50 KB on local disk).
#if NETSTANDARD2_0
                        using (var jsonStream = fileAccessor.OpenWrite(workflowJsonTmp))
                        using (var writer = new StreamWriter(jsonStream, Encoding.UTF8))
                        {
                            await writer.WriteAsync(jsonString).ConfigureAwait(false);
                        }
#else
                        var jsonStream = fileAccessor.OpenWrite(workflowJsonTmp);
                        await using (jsonStream.ConfigureAwait(false))
                        {
                            var writer = new StreamWriter(jsonStream, Encoding.UTF8);
                            await using (writer.ConfigureAwait(false))
                            {
                                await writer.WriteAsync(jsonString).ConfigureAwait(false);
                            }
                        }
#endif
                        fileAccessor.Replace(workflowJsonTmp, workflowJson);
                    }

                    var workflowMetadata = new AgentFilePath($"{workflowFolder}/metadata.yml");
                    var workflowMetadataTmp = new AgentFilePath($"{workflowFolder}/metadata.yml.tmp");
                    var serializer = new SerializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).Build();
                    var metadataString = serializer.Serialize(workflow);

                    if (await WorkflowFileNeedsWriteAsync(fileAccessor, workflowMetadata, metadataString, cancellationToken).ConfigureAwait(false))
                    {
#if NETSTANDARD2_0
                        using (var metaStream = fileAccessor.OpenWrite(workflowMetadataTmp))
                        using (var writer = new StreamWriter(metaStream, Encoding.UTF8))
                        {
                            writer.Write(metadataString);
                        }
#else
                        var metaStream = fileAccessor.OpenWrite(workflowMetadataTmp);
                        await using (metaStream.ConfigureAwait(false))
                        {
                            var writer = new StreamWriter(metaStream, Encoding.UTF8);
                            await using (writer.ConfigureAwait(false))
                            {
                                await writer.WriteAsync(metadataString).ConfigureAwait(false);
                            }
                        }
#endif
                        fileAccessor.Replace(workflowMetadataTmp, workflowMetadata);
                    }
                }
            }

            connectionReferences = await GetConnectionReferenceFromLogicalNamesAsync(GetConnectionReferenceLogicalNamesFromFlows(workflows), dataverseClient, cancellationToken).ConfigureAwait(false);

            // Remove local workflow if cloud workflow is removed.
            var existingWorkflowIds = existingFolders.Keys.ToHashSet();
            var cloudWorkflowIds = new HashSet<Guid>(cloudFlowDefinitions.Select(w => w.WorkflowId.Value));
            var workflowsToDelete = existingWorkflowIds.Except(cloudWorkflowIds);

            foreach (var workflowIdToDelete in workflowsToDelete)
            {
                if (existingFolders.TryGetValue(workflowIdToDelete, out var folderToDelete) && Directory.Exists(folderToDelete))
                {
                    Directory.Delete(folderToDelete, true);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _syncProgress.Report($"Failed to download workflows for agent {syncInfo.AgentId}. Exception: {ex.Message}");
        }

        return new CloudFlowMetadata
        {
            Workflows = cloudFlowDefinitions.ToImmutableArray(),
            ConnectionReferences = connectionReferences
        };
    }

    public virtual async Task<ImmutableArray<AIPromptMetadata>> GetAIPromptsAsync(DirectoryPath workspaceFolder, ISyncDataverseClient dataverseClient, AgentSyncInfo syncInfo, IFileAccessor fileAccessor, CancellationToken cancellationToken)
    {
        var prompts = ImmutableArray.CreateBuilder<AIPromptMetadata>();

        try
        {
            var remotePrompts = await dataverseClient.DownloadAllAIPromptsForAgentAsync(syncInfo, cancellationToken).ConfigureAwait(false);
            var promptsRoot = Path.Combine(workspaceFolder.ToString(), PromptsFolder);

            var existingFolders = new Dictionary<Guid, string>();
            if (Directory.Exists(promptsRoot))
            {
                foreach (var folder in Directory.EnumerateDirectories(promptsRoot))
                {
                    var modelId = ExtractTrailingGuidFromFileName(Path.GetFileName(folder));
                    if (modelId.HasValue)
                    {
                        existingFolders[modelId.Value] = folder;
                    }
                }
            }

            if (remotePrompts == null || remotePrompts.Length == 0)
            {
                foreach (var folder in existingFolders.Values)
                {
                    if (Directory.Exists(folder))
                    {
                        Directory.Delete(folder, true);
                    }
                }

                return ImmutableArray<AIPromptMetadata>.Empty;
            }

            Directory.CreateDirectory(promptsRoot);

            foreach (var prompt in remotePrompts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                prompts.Add(prompt);

                var folderName = $"{SanitizeFolderSegment(prompt.Name ?? string.Empty)}-{prompt.AIModelId}";
                var folderPath = Path.Combine(promptsRoot, folderName);

                if (existingFolders.TryGetValue(prompt.AIModelId, out var existingFolderPath))
                {
                    if (!string.Equals(existingFolderPath, folderPath, StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            Directory.Move(existingFolderPath, folderPath);
                        }
                        catch (IOException) when (Directory.Exists(folderPath))
                        {
                            Directory.Delete(folderPath, true);
                            Directory.Move(existingFolderPath, folderPath);
                        }
                    }
                    existingFolders[prompt.AIModelId] = folderPath;
                }
                else
                {
                    Directory.CreateDirectory(folderPath);
                    existingFolders[prompt.AIModelId] = folderPath;
                }

                var promptFolderRelative = Path.Combine(PromptsFolder, folderName).Replace("\\", "/");

                if (!string.IsNullOrWhiteSpace(prompt.CustomConfiguration))
                {
                    var promptJsonPath = new AgentFilePath($"{promptFolderRelative}/prompt.json");
                    var promptJsonTempPath = new AgentFilePath($"{promptFolderRelative}/prompt.json.tmp");

                    var jsonString = BuildPromptJson(prompt.Name, prompt.CustomConfiguration!);

#if NETSTANDARD2_0
                    using (var jsonStream = fileAccessor.OpenWrite(promptJsonTempPath))
                    using (var writer = new StreamWriter(jsonStream, Encoding.UTF8))
                    {
                        await writer.WriteAsync(jsonString).ConfigureAwait(false);
                    }
#else
                    var jsonStream = fileAccessor.OpenWrite(promptJsonTempPath);
                    await using (jsonStream.ConfigureAwait(false))
                    {
                        var writer = new StreamWriter(jsonStream, Encoding.UTF8);
                        await using (writer.ConfigureAwait(false))
                        {
                            await writer.WriteAsync(jsonString).ConfigureAwait(false);
                        }
                    }
#endif
                    fileAccessor.Replace(promptJsonTempPath, promptJsonPath);
                }

                var promptMetadataPath = new AgentFilePath($"{promptFolderRelative}/metadata.yml");
                var promptMetadataTempPath = new AgentFilePath($"{promptFolderRelative}/metadata.yml.tmp");
                var serializer = new SerializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).Build();

#if NETSTANDARD2_0
                using (var metadataStream = fileAccessor.OpenWrite(promptMetadataTempPath))
                using (var writer = new StreamWriter(metadataStream, Encoding.UTF8))
                {
                    serializer.Serialize(writer, prompt);
                }
#else
                var metadataStream = fileAccessor.OpenWrite(promptMetadataTempPath);
                await using (metadataStream.ConfigureAwait(false))
                {
                    var writer = new StreamWriter(metadataStream, Encoding.UTF8);
                    await using (writer.ConfigureAwait(false))
                    {
                        serializer.Serialize(writer, prompt);
                    }
                }
#endif
                fileAccessor.Replace(promptMetadataTempPath, promptMetadataPath);
            }

            var remoteIds = remotePrompts.Select(prompt => prompt.AIModelId).ToHashSet();
            foreach (var existingFolder in existingFolders)
            {
                if (!remoteIds.Contains(existingFolder.Key) && Directory.Exists(existingFolder.Value))
                {
                    Directory.Delete(existingFolder.Value, true);
                }
            }
        }
        catch (Exception ex)
        {
            _syncProgress.Report($"Failed to download AI prompts for agent {syncInfo.AgentId}. Exception: {ex.Message}");
        }

        return prompts.ToImmutable();
    }

    public virtual async Task<(ImmutableArray<AIPromptResponse>, ImmutableArray<AIPromptMetadata>)> UpsertAIPromptsForAgentAsync(DirectoryPath workspaceFolder, ISyncDataverseClient dataverseClient, Guid? agentId, CancellationToken cancellationToken)
    {
        var responses = ImmutableArray.CreateBuilder<AIPromptResponse>();
        var prompts = ImmutableArray.CreateBuilder<AIPromptMetadata>();
        var promptsDir = Path.Combine(workspaceFolder.ToString(), PromptsFolder);
        if (!Directory.Exists(promptsDir))
        {
            return (responses.ToImmutable(), prompts.ToImmutable());
        }

        foreach (var promptFolder in Directory.EnumerateDirectories(promptsDir))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folderName = Path.GetFileName(promptFolder);
            var aiModelId = ExtractTrailingGuidFromFileName(folderName);
            if (aiModelId == null)
            {
                continue;
            }

            var promptJsonFile = Path.Combine(promptFolder, "prompt.json");
            var metadataFile = Path.Combine(promptFolder, "metadata.yml");
            if (!File.Exists(metadataFile))
            {
                continue;
            }

            var yamlText = await FileShim.ReadAllTextAsync(metadataFile, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            var deserializer = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).IgnoreUnmatchedProperties().Build();
            AIPromptMetadata metadata;
            try
            {
                metadata = deserializer.Deserialize<AIPromptMetadata>(yamlText) ?? new AIPromptMetadata();
            }
            catch (Exception ex)
            {
                _syncProgress.Report($"Failed to parse {metadataFile}: {ex.Message}");
                continue;
            }

            metadata.AIModelId = aiModelId.Value;

            if (File.Exists(promptJsonFile))
            {
                var promptJsonText = await FileShim.ReadAllTextAsync(promptJsonFile, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
                metadata.CustomConfiguration = BuildCustomConfigurationFromPromptJson(promptJsonText);

                var promptName = TryReadPromptName(promptJsonText);
                if (!string.IsNullOrWhiteSpace(promptName))
                {
                    metadata.Name = promptName;
                }
            }

            var response = await dataverseClient.UpsertAIPromptAsync(agentId, metadata, cancellationToken).ConfigureAwait(false);
            responses.Add(response);

            if (string.IsNullOrEmpty(response.ErrorMessage))
            {
                prompts.Add(metadata);
            }
            else
            {
                _syncProgress.Report($"AI prompt '{response.PromptName}' publish failed; error: {response.ErrorMessage}");
            }
        }

        return (responses.ToImmutable(), prompts.ToImmutable());
    }

    private static ImmutableArray<AIModelDefinition> BuildAIModelDefinitions(ImmutableArray<AIPromptMetadata> prompts)
    {
        if (prompts.IsDefaultOrEmpty)
        {
            return ImmutableArray<AIModelDefinition>.Empty;
        }

        var builder = ImmutableArray.CreateBuilder<AIModelDefinition>(prompts.Length);
        foreach (var prompt in prompts)
        {
            if (prompt.AIModelId == Guid.Empty)
            {
                continue;
            }

            var (inputType, outputType) = ExtractAIPromptIO(prompt.CustomConfiguration);

            builder.Add(new AIModelDefinition(
                id: new AIModelId(prompt.AIModelId),
                name: prompt.Name,
                inputType: inputType,
                outputType: outputType));
        }
        return builder.ToImmutable();
    }

    internal static (RecordDataType? inputType, RecordDataType? outputType) ExtractAIPromptIO(string? customConfiguration)
    {
        if (string.IsNullOrWhiteSpace(customConfiguration))
        {
            return (null, null);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(customConfiguration!);
        }
        catch (JsonException)
        {
            return (null, null);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("definitions", out var definitionsElement) || definitionsElement.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            RecordDataType? inputType = null;
            if (definitionsElement.TryGetProperty("inputs", out var inputsElement))
            {
                inputType = BuildRecordDataTypeFromAIPromptInputs(inputsElement);
            }

            RecordDataType? outputType = null;
            if (definitionsElement.TryGetProperty("output", out var outputElement))
            {
                outputType = BuildRecordDataTypeFromAIPromptOutput(outputElement);
            }

            return (inputType, outputType);
        }
    }

    private static RecordDataType? BuildRecordDataTypeFromAIPromptInputs(JsonElement inputsElement)
    {
        var properties = new Dictionary<string, PropertyInfo>(StringComparer.OrdinalIgnoreCase);

        if (inputsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var input in inputsElement.EnumerateArray())
            {
                if (input.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                var name = (input.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String ? nameElement.GetString() : null)
                    ?? (input.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String ? idElement.GetString() : null);
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }
                properties[name!] = CreatePropertyInfoFromJson(input, name!);
            }
        }
        else if (inputsElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var input in inputsElement.EnumerateObject())
            {
                properties[input.Name] = CreatePropertyInfoFromJson(input.Value, input.Name);
            }
        }

        return properties.Count == 0 ? null : new RecordDataType(properties.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase));
    }

    private const string AIPromptOutputBindingName = "predictionOutput";

    private static RecordDataType BuildRecordDataTypeFromAIPromptOutput(JsonElement outputElement)
    {
        var predictionOutputFields = new Dictionary<string, PropertyInfo>(StringComparer.OrdinalIgnoreCase)
        {
            ["text"] = new PropertyInfo(displayName: "text", description: null, isRequired: false, type: DataType.String),
            ["finishReason"] = new PropertyInfo(displayName: "finishReason", description: null, isRequired: false, type: DataType.String),
            ["dataUsed"] = new PropertyInfo(displayName: "dataUsed", description: null, isRequired: false, type: DataType.String),
        };

        if (outputElement.ValueKind == JsonValueKind.Object && outputElement.TryGetProperty("jsonSchema", out var jsonSchemaElement) && jsonSchemaElement.ValueKind == JsonValueKind.Object && jsonSchemaElement.TryGetProperty("properties", out var schemaPropertiesElement) && schemaPropertiesElement.ValueKind == JsonValueKind.Object)
        {
            var structuredRecord = BuildRecordDataTypeFromJsonSchemaProperties(schemaPropertiesElement);
            if (structuredRecord != null)
            {
                predictionOutputFields["structuredOutput"] = new PropertyInfo(displayName: "structuredOutput", description: null, isRequired: false, type: structuredRecord);
            }
        }

        var predictionOutputRecord = new RecordDataType(predictionOutputFields.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase));
        var rootFields = new Dictionary<string, PropertyInfo>(StringComparer.OrdinalIgnoreCase)
        {
            [AIPromptOutputBindingName] = new PropertyInfo(
                displayName: AIPromptOutputBindingName,
                description: null,
                isRequired: false,
                type: predictionOutputRecord),
        };

        return new RecordDataType(rootFields.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase));
    }

    private static RecordDataType? BuildRecordDataTypeFromJsonSchemaProperties(JsonElement propertiesElement)
    {
        if (propertiesElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var properties = new Dictionary<string, PropertyInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in propertiesElement.EnumerateObject())
        {
            properties[ProcessAIPromptOutputName(prop.Name)] = BuildPropertyInfoFromJsonSchema(prop.Value, prop.Name);
        }

        return properties.Count == 0 ? null : new RecordDataType(properties.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase));
    }

    // Process AI prompt output property names that are not valid (ex: contain spaces or other punctuation). Topics author against
    // Rule:
    //   - If the property name is already a valid identifier ([A-Za-z_][A-Za-z0-9_]*), keep it unchanged.
    //   - Otherwise, XML-style name-encode the original: each non-alphanumeric char becomes "_XXXX" (4 hex digits, uppercase, of the char code).
    //     Take the first 8 chars of that encoded string as the prefix, then append SHA256(UTF-8(originalName))[:32] hex.
    // Examples:
    //   "Due-Date"                        -> "Due_002Dda5f9e59e67c82f09c296caa2bfca354"
    //   "date description"                -> "date_002da27979fcb9686b5b8261c3d2a79ec84"
    //   "shiping method"                  -> "shiping_588f41922a4dc30d2d1c4654fd7b6fd7"
    //   "Container 1 Registration Number" -> "Containe2e5985a0512b7b685a45b21ef6350c0d"
    internal static string ProcessAIPromptOutputName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }

        if (System.Text.RegularExpressions.Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_]*$"))
        {
            return name;
        }

        var encoded = new StringBuilder(name.Length * 2);
        foreach (var c in name)
        {
            if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))
            {
                encoded.Append(c);
            }
            else
            {
                encoded.Append('_').Append(((int)c).ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        var prefix = encoded.Length >= 8 ? encoded.ToString(0, 8) : encoded.ToString().PadRight(8, '_');

        using var sha = System.Security.Cryptography.SHA256.Create();
        var hashBytes = sha.ComputeHash(Encoding.UTF8.GetBytes(name));
        var hashHex = new StringBuilder(64);
        foreach (var b in hashBytes)
        {
            hashHex.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        }

        return prefix + hashHex.ToString(0, 32);
    }

    private static PropertyInfo BuildPropertyInfoFromJsonSchema(JsonElement schemaElement, string propName)
    {
        var displayName = schemaElement.ValueKind == JsonValueKind.Object && schemaElement.TryGetProperty("title", out var titleNode) && titleNode.ValueKind == JsonValueKind.String ? titleNode.GetString() : propName;
        var description = schemaElement.ValueKind == JsonValueKind.Object && schemaElement.TryGetProperty("description", out var descNode) && descNode.ValueKind == JsonValueKind.String ? descNode.GetString() : null;
        DataType type = MapJsonSchemaType(schemaElement);
        return new PropertyInfo(displayName: displayName, description: description, isRequired: false, type: type);
    }

    private static DataType MapJsonSchemaType(JsonElement schemaElement)
    {
        if (schemaElement.ValueKind != JsonValueKind.Object)
        {
            return DataType.String;
        }

        var schemaType = schemaElement.TryGetProperty("type", out var typeNode) && typeNode.ValueKind == JsonValueKind.String ? typeNode.GetString() : null;

        if (string.Equals(schemaType, "object", StringComparison.OrdinalIgnoreCase) && schemaElement.TryGetProperty("properties", out var nestedProps))
        {
            return BuildRecordDataTypeFromJsonSchemaProperties(nestedProps) ?? DataType.EmptyRecord;
        }

        if (string.Equals(schemaType, "array", StringComparison.OrdinalIgnoreCase))
        {
            if (schemaElement.TryGetProperty("items", out var itemsElement) && itemsElement.ValueKind == JsonValueKind.Object)
            {
                var itemSchemaType = itemsElement.TryGetProperty("type", out var itemTypeNode) && itemTypeNode.ValueKind == JsonValueKind.String ? itemTypeNode.GetString() : null;

                if (string.Equals(itemSchemaType, "object", StringComparison.OrdinalIgnoreCase) && itemsElement.TryGetProperty("properties", out var itemProps) && itemProps.ValueKind == JsonValueKind.Object)
                {
                    var rowProps = new Dictionary<string, PropertyInfo>(StringComparer.OrdinalIgnoreCase);
                    foreach (var p in itemProps.EnumerateObject())
                    {
                        rowProps[ProcessAIPromptOutputName(p.Name)] = BuildPropertyInfoFromJsonSchema(p.Value, p.Name);
                    }

                    return new TableDataType(rowProps.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase));
                }

                var scalarColumn = new Dictionary<string, PropertyInfo>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Value"] = new PropertyInfo(displayName: "Value", description: null, isRequired: false, type: MapJsonSchemaType(itemsElement))
                };
                return new TableDataType(scalarColumn.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase));
            }

            return DataType.EmptyTable;
        }

        return MapFlowType(schemaType ?? "string");
    }

    internal static string SanitizeFolderSegment(string raw)
    {
        return new string(raw.Where(character => !Path.GetInvalidFileNameChars().Contains(character) && !char.IsWhiteSpace(character)).ToArray()).TrimEnd('.', ' ');
    }

    internal static Guid? ExtractTrailingGuidFromFileName(string fileName)
    {
        var match = System.Text.RegularExpressions.Regex.Match(fileName, @"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})$");
        if (match.Success && Guid.TryParse(match.Value, out var parsedGuid))
        {
            return parsedGuid;
        }
        return null;
    }

    private const string PromptInputPlaceholderPattern = @"\{\{([A-Za-z0-9_\-]+)\}\}";

    internal static string BuildPromptJson(string? promptName, string rawCustomConfiguration)
    {
        JsonDocument rawDocument;
        try
        {
            rawDocument = JsonDocument.Parse(rawCustomConfiguration);
        }
        catch (JsonException)
        {
            return rawCustomConfiguration;
        }

        using (rawDocument)
        {
            var rootElement = rawDocument.RootElement;
            var result = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);

            if (!string.IsNullOrWhiteSpace(promptName))
            {
                result["name"] = promptName;
            }

            if (rootElement.TryGetProperty("prompt", out var promptArrayElement) && promptArrayElement.ValueKind == JsonValueKind.Array)
            {
                var instructionBuilder = new StringBuilder();
                foreach (var segment in promptArrayElement.EnumerateArray())
                {
                    var segmentType = segment.TryGetProperty("type", out var segmentTypeElement) ? segmentTypeElement.GetString() : null;
                    if (string.Equals(segmentType, "literal", StringComparison.Ordinal))
                    {
                        if (segment.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String)
                        {
                            instructionBuilder.Append(textElement.GetString());
                        }
                    }
                    else if (string.Equals(segmentType, "inputVariable", StringComparison.Ordinal))
                    {
                        if (segment.TryGetProperty("id", out var inputIdElement) && inputIdElement.ValueKind == JsonValueKind.String)
                        {
                            instructionBuilder.Append("{{").Append(inputIdElement.GetString()).Append("}}");
                        }
                    }
                }

                result["instruction"] = instructionBuilder.ToString();
            }

            if (rootElement.TryGetProperty("modelParameters", out var modelParametersElement) && modelParametersElement.ValueKind == JsonValueKind.Object)
            {
                if (modelParametersElement.TryGetProperty("modelType", out var modelTypeElement) && modelTypeElement.ValueKind == JsonValueKind.String)
                {
                    result["model"] = modelTypeElement.GetString();
                }

                var extraModelParameters = new JsonObject();
                foreach (var parameter in modelParametersElement.EnumerateObject())
                {
                    if (string.Equals(parameter.Name, "modelType", StringComparison.Ordinal)) continue;
                    extraModelParameters[parameter.Name] = JsonNode.Parse(parameter.Value.GetRawText());
                }

                if (extraModelParameters.Count > 0)
                {
                    result["modelParameters"] = extraModelParameters;
                }
            }

            if (rootElement.TryGetProperty("definitions", out var definitionsElement) && definitionsElement.ValueKind == JsonValueKind.Object)
            {
                if (definitionsElement.TryGetProperty("inputs", out var inputsElement))
                {
                    result["inputs"] = JsonNode.Parse(inputsElement.GetRawText());
                }

                if (definitionsElement.TryGetProperty("output", out var outputElement))
                {
                    result["output"] = JsonNode.Parse(outputElement.GetRawText());
                }

                if (definitionsElement.TryGetProperty("formulas", out var formulasElement) && formulasElement.ValueKind == JsonValueKind.Array && formulasElement.GetArrayLength() > 0)
                {
                    result["formulas"] = JsonNode.Parse(formulasElement.GetRawText());
                }

                if (definitionsElement.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Array && dataElement.GetArrayLength() > 0)
                {
                    result["data"] = JsonNode.Parse(dataElement.GetRawText());
                }
            }

            if (rootElement.TryGetProperty("settings", out var settingsElement))
            {
                result["settings"] = JsonNode.Parse(settingsElement.GetRawText());
            }

            if (rootElement.TryGetProperty("version", out var versionElement) && versionElement.ValueKind == JsonValueKind.String)
            {
                result["version"] = versionElement.GetString();
            }

            if (rootElement.TryGetProperty("code", out var codeElement) && codeElement.ValueKind == JsonValueKind.String)
            {
                var codeText = codeElement.GetString();
                if (!string.IsNullOrEmpty(codeText))
                {
                    result["code"] = codeText;
                }
            }

            if (rootElement.TryGetProperty("signature", out var signatureElement) && signatureElement.ValueKind == JsonValueKind.String)
            {
                var signatureText = signatureElement.GetString();
                if (!string.IsNullOrEmpty(signatureText))
                {
                    result["signature"] = signatureText;
                }
            }

            var resultObject = new JsonObject();
            foreach (var entry in result)
            {
                resultObject[entry.Key] = entry.Value;
            }

            return resultObject.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }
    }

    internal static string BuildCustomConfigurationFromPromptJson(string promptJsonText)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(promptJsonText);
        }
        catch (JsonException)
        {
            return promptJsonText;
        }

        using (document)
        {
            var rootElement = document.RootElement;
            var looksPortalShape = rootElement.ValueKind == JsonValueKind.Object && (rootElement.TryGetProperty("instruction", out _) || rootElement.TryGetProperty("inputs", out _) || rootElement.TryGetProperty("output", out _) || rootElement.TryGetProperty("model", out _));
            var looksRaw = rootElement.ValueKind == JsonValueKind.Object && rootElement.TryGetProperty("prompt", out _) && rootElement.TryGetProperty("definitions", out _);

            if (!looksPortalShape && looksRaw)
            {
                return promptJsonText;
            }

            var rawObject = new JsonObject
            {
                ["version"] = rootElement.TryGetProperty("version", out var versionElement) && versionElement.ValueKind == JsonValueKind.String ? versionElement.GetString() : "GptDynamicPrompt-2"
            };

            var promptArray = new JsonArray();
            if (rootElement.TryGetProperty("instruction", out var instructionElement) && instructionElement.ValueKind == JsonValueKind.String)
            {
                var instruction = instructionElement.GetString() ?? string.Empty;
                var placeholderRegex = new System.Text.RegularExpressions.Regex(PromptInputPlaceholderPattern);
                var lastIndex = 0;
                foreach (System.Text.RegularExpressions.Match placeholderMatch in placeholderRegex.Matches(instruction))
                {
                    if (placeholderMatch.Index > lastIndex)
                    {
                        promptArray.Add(new JsonObject
                        {
                            ["type"] = "literal",
                            ["text"] = instruction.Substring(lastIndex, placeholderMatch.Index - lastIndex)
                        });
                    }
                    promptArray.Add(new JsonObject
                    {
                        ["type"] = "inputVariable",
                        ["id"] = placeholderMatch.Groups[1].Value
                    });
                    lastIndex = placeholderMatch.Index + placeholderMatch.Length;
                }
                if (lastIndex < instruction.Length)
                {
                    promptArray.Add(new JsonObject
                    {
                        ["type"] = "literal",
                        ["text"] = instruction.Substring(lastIndex)
                    });
                }
            }
            rawObject["prompt"] = promptArray;

            var definitions = new JsonObject
            {
                ["inputs"] = rootElement.TryGetProperty("inputs", out var inputsElement) ? JsonNode.Parse(inputsElement.GetRawText()) : new JsonArray(),
                ["formulas"] = rootElement.TryGetProperty("formulas", out var formulasElement) ? JsonNode.Parse(formulasElement.GetRawText()) : new JsonArray(),
                ["data"] = rootElement.TryGetProperty("data", out var dataElement) ? JsonNode.Parse(dataElement.GetRawText()) : new JsonArray()
            };

            if (rootElement.TryGetProperty("output", out var outputElement))
            {
                definitions["output"] = JsonNode.Parse(outputElement.GetRawText());
            }

            rawObject["definitions"] = definitions;

            var modelParameters = rootElement.TryGetProperty("modelParameters", out var modelParametersElement) && modelParametersElement.ValueKind == JsonValueKind.Object ? (JsonObject)JsonNode.Parse(modelParametersElement.GetRawText())! : new JsonObject();
            if (rootElement.TryGetProperty("model", out var modelElement) && modelElement.ValueKind == JsonValueKind.String)
            {
                modelParameters["modelType"] = modelElement.GetString();
            }
            rawObject["modelParameters"] = modelParameters;

            if (rootElement.TryGetProperty("settings", out var settingsElement))
            {
                rawObject["settings"] = JsonNode.Parse(settingsElement.GetRawText());
            }

            rawObject["code"] = rootElement.TryGetProperty("code", out var codeElement) && codeElement.ValueKind == JsonValueKind.String ? codeElement.GetString() ?? string.Empty : string.Empty;
            rawObject["signature"] = rootElement.TryGetProperty("signature", out var signatureElement) && signatureElement.ValueKind == JsonValueKind.String ? signatureElement.GetString() ?? string.Empty : string.Empty;

            return rawObject.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        }
    }

    internal static string? TryReadPromptName(string promptJsonText)
    {
        try
        {
            using var document = JsonDocument.Parse(promptJsonText);
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String)
            {
                return nameElement.GetString();
            }
        }
        catch (JsonException)
        {
        }
        return null;
    }

    public async Task<DefinitionBase> ReadWorkspaceDefinitionAsync(DirectoryPath workspaceFolder, CancellationToken cancellationToken, bool checkKnowledgeFiles = false)
    {
        var fileAccessor = _fileAccessorFactory.Create(workspaceFolder);
        var definition = ReadCloudCacheSnapshot(fileAccessor, allowMissing: true);
        if (definition == null)
        {
            definition = ReadCachelessCliDefinitionOrNull(fileAccessor);
            if (definition == null)
            {
                throw new FileNotFoundException(".mcs/botdefinition.json was not found. Please resync.");
            }
        }

        // CliAgentSyncSupport / Node E: compute kind ONCE per read so every
        // per-component decision uses the same discriminator. Mirrors the
        // single-chokepoint pattern from UpdateWorkspaceDirectoryAsync, routed
        // through the shared AgentClassifier (PRD R1).
        var isCliAgent = definition is BotDefinition botForKind
                         && botForKind.Entity != null
                         && IsCliAgentEntity(botForKind.Entity);

        // CliAgentSyncSupport / Node F: "CLI layout adopted" gate. Used to
        // gate destructive per-component / connection-reference delete
        // detection later in this method, and (transitively) in
        // GetLocalChanges. Computed once here. Note: this is the agent.yaml
        // exists check — stronger than per-route IsActive signals
        // (because deleting the only file in a route would disable the
        // per-route signal).
        var isCliLayoutAdopted = isCliAgent && CliAgentBotEntityReader.IsCliLayoutAdopted(fileAccessor);

        // CliAgentSyncSupport / Node Q (D30, old-layout-no-nuke): the workspace has
        // adopted the new .mcs.yml component layout when it is a CLI agent with the
        // settings.mcs.yml entity present AND no legacy bare-body .yaml component
        // files linger in the three-layer folders. Only then may a missing
        // component file be interpreted as a user delete; an old-layout workspace
        // (whose components are .yaml, written by pre-Q code) must instead preserve
        // the cloud cache so the new reader never synthesizes a phantom delete.
        var cliNewLayoutAdopted = isCliLayoutAdopted && !HasLegacyCliYamlComponentFiles(fileAccessor);

        // CliAgentSyncSupport / Node F: agent.yaml overlay. When the
        // workspace has adopted the CLI layout (agent.yaml exists), overlay
        // identity + recognizer + agentSettings from disk onto the
        // cloud-cache entity. Hard-fails on malformed agent.yaml or
        // schemaName change — see CliAgentBotEntityReader for rationale.
        if (isCliLayoutAdopted && definition is BotDefinition botForOverlay && botForOverlay.Entity != null)
        {
            var overlaidEntity = CliAgentBotEntityReader.Overlay(fileAccessor, botForOverlay.Entity);
            definition = botForOverlay.WithEntity(overlaidEntity);
        }

        var updatedComponents = new List<BotComponentBase>();
        var existingSchemaNames = new HashSet<string>();

        foreach (var component in definition.Components)
        {
            cancellationToken.ThrowIfCancellationRequested();

            existingSchemaNames.Add(component.SchemaNameString);

            if (IsReusableOrNonCustomizableComponent(component))
            {
                continue;
            }

            // CliAgentSyncSupport / Node Q (D20/D30): the reader resolves every
            // component (CLI + classic) through the SAME shape-aware
            // PathResolver.GetComponentPath the writer used, so read/write/delete
            // stay in agreement and the reader never synthesizes a phantom delete.
            var filePath = new AgentFilePath(_pathResolver.GetComponentPath(component, definition));

            if (!fileAccessor.Exists(filePath))
            {
                if (isCliAgent && !cliNewLayoutAdopted)
                {
                    // Old/transition CLI layout: pre-Q code wrote component bodies
                    // as bare .yaml (not .mcs.yml), so a missing .mcs.yml is NOT a
                    // user delete. Preserve the cloud-cache component; reading an
                    // old-layout workspace with new code must never nuke it
                    // (re-clone migrates to the .mcs.yml layout). [old-layout-no-nuke]
                    updatedComponents.Add(component);
                    continue;
                }

                // Classic, or an adopted CLI .mcs.yml layout: the user deleted the
                // file -> drop the component (synthesize delete intent). [delete-safety]
                continue;
            }

            string yaml;
            BotElement? deserialized;
            try
            {
                using var stream = fileAccessor.OpenRead(filePath);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                // No #if: small component file; boundary cancellation is sufficient.
                cancellationToken.ThrowIfCancellationRequested();
                yaml = await reader.ReadToEndAsync().ConfigureAwait(false);

                deserialized = CodeSerializer.Deserialize(yaml, component.RootElement?.GetType() ?? typeof(BotElement), null);
            }
            catch (Exception ex) when (isCliAgent && ex is not OperationCanceledException)
            {
                // CliAgentSyncSupport / Node E (rubber-duck non-blocking #4):
                // CLI-path deserialize errors must not abort the entire
                // read, but cancellation must still propagate. Scope the
                // skip-and-warn to CLI files only (classic shape was
                // hard-fail before Node E and stays hard-fail to preserve
                // pre-existing behavior).
                _syncProgress.Report(
                    $"CLI component file '{filePath}' could not be read or parsed: {ex.Message}. Keeping cloud-cache version.");
                updatedComponents.Add(component);
                continue;
            }

            var (parsed, error) = _fileParser.CompileFileModel(component.SchemaNameString, deserialized, component.DisplayName, component.Description);

            if (error != null || parsed == null)
            {
                // Fall back to cloud cache version if file cannot be parsed
                updatedComponents.Add(component);
                continue;
            }

            // Copy metadata from cloud cache component
            var builder = parsed.ToBuilder();
            builder.Id = component.Id;
            builder.Version = component.Version;
            builder.ParentBotId = component.ParentBotId;
            builder.ParentBotComponentId = component.ParentBotComponentId;
            builder.ParentBotComponentCollectionId = component.ParentBotComponentCollectionId;
            updatedComponents.Add(builder.Build());
        }

        // CliAgentSyncSupport / Node E (rubber-duck non-blocking #5): gate
        // the classic *.mcs.yml new-file scan when the workspace is a CLI
        // agent. On CLI workspaces, the only *.mcs.yml files would be stale
        // siblings from a pre-CLI-rewrite clone (D1 deletes settings.mcs.yml
        // but does not sweep topics/, knowledge/, etc.). Treating those as
        // "new local files" would synthesize phantom components that the
        // next push would then attempt to insert into the cloud — clearly
        // wrong. New-CLI-file discovery (capabilities/tools/foo.yaml, etc.)
        // is a push-side problem handled by ScanForNewCliFiles below.
        if (!isCliAgent)
        {
            // Detect new local files
            var knownPaths = definition.Components.Select(c => _pathResolver.GetComponentPath(c, definition)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var localFiles = fileAccessor.ListFiles(filePattern: "*.mcs.yml").ToList();
            var newLocalFiles = localFiles.Where(f => !knownPaths.Contains(f.ToString())).ToList();

            if (newLocalFiles.Count != 0)
            {
                var projectionContext = new ProjectionContext(GetSchemaName(definition));

                foreach (var localFile in newLocalFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var yaml = await fileAccessor.ReadStringAsync(localFile, cancellationToken).ConfigureAwait(false);

                    if (CodeSerializer.Deserialize(yaml, typeof(BotElement), null) is not BotElement element)
                    {
                        continue;
                    }

                    var (component, error) = _fileParser.CompileFile(localFile, element, projectionContext);

                    if (component != null && error == null)
                    {
                        updatedComponents.Add(component);
                    }
                }
            }
        }
        else
        {
            // CliAgentSyncSupport / Node F: scan CLI route directories for
            // new files the user authored locally (no cloud-cache match).
            // Mirrors the classic new-file scan but uses the route-specific
            // filename → schemaName projections (TryProjectSchemaNameFromFilePath).
            ScanForNewCliFiles(fileAccessor, definition, updatedComponents, existingSchemaNames, cancellationToken);
        }

        if (checkKnowledgeFiles)
        {
            // Detect new knowledge files. Shape-keyed scan folder (TDD D34): CLI knowledge
            // content lives at capabilities/knowledge/files/, classic at knowledge/files/.
            var knowledgeFilesSubPath = isCliAgent ? CliKnowledgeFilesSubPath : KnowledgeFilesSubPath;
            var knowledgeFiles = fileAccessor.ListFiles(knowledgeFilesSubPath, "*.*").Where(f => !f.FileName.EndsWith(".mcs.yml", StringComparison.OrdinalIgnoreCase)).ToList();
            var existingKnowledgeNames = definition.Components.OfType<FileAttachmentComponent>().Select(c => c.DisplayName).Where(n => !string.IsNullOrEmpty(n)).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var file in knowledgeFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (existingKnowledgeNames.Contains(file.FileName))
                {
                    continue;
                }

                if (IsValidFileToUpload(fileAccessor, file))
                {
                    var schemaName = SchemaNameGenerator.GenerateSchemaNameForBotComponent(
                        botSchemaPrefix: GetSchemaName(definition),
                        componentPrefix: "file",
                        componentDisplayName: file.FileName,
                        existingSchemaNames: existingSchemaNames
                    );

                    var component = new FileAttachmentComponent()
                                    .WithSchemaName(schemaName)
                                    .WithDisplayName(file.FileName)
                                    .WithDescription($"This knowledge source searches information contained in {file.FileName}");

                    updatedComponents.Add(component);
                }
            }
        }

        // Read environment variables from environmentvariables/*.mcs.yml
        var updatedEnvVars = await ReadEnvironmentVariablesAsync(fileAccessor, definition, cancellationToken).ConfigureAwait(false);

        // CliAgentSyncSupport / Node E: overlay on-disk connection refs
        // (per-reference layered shape from D5) onto cloud-cache. Gated by
        // the migration-safe activation rule — see CliAgentConnectionsReader
        // for rationale on why we keep cache verbatim when the directory
        // is empty or absent.
        var updatedConnectionRefs = definition.ConnectionReferences;
        if (isCliAgent && CliAgentConnectionsReader.IsLayeredShapeActive(fileAccessor))
        {
            var overlaid = CliAgentConnectionsReader.Overlay(
                fileAccessor,
                definition.ConnectionReferences,
                _syncProgress.Report,
                cancellationToken);
            updatedConnectionRefs = overlaid.ToImmutableArray();
        }

        return definition
            .WithComponents(updatedComponents)
            .WithEnvironmentVariables(updatedEnvVars)
            .WithConnectionReferences(updatedConnectionRefs);
    }

    private static DefinitionBase? ReadCachelessCliDefinitionOrNull(IFileAccessor fileAccessor)
    {
        if (!fileAccessor.Exists(SettingsPath))
        {
            return null;
        }

        string yaml;
        try
        {
            using var stream = fileAccessor.OpenRead(SettingsPath);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            yaml = reader.ReadToEnd();
        }
        catch (Exception ex)
        {
            if (fileAccessor.Exists(AgentSyncMarkerPath))
            {
                throw new InvalidOperationException(
                    $"CLI settings.mcs.yml could not be read from a cacheless workspace: {ex.Message}.",
                    ex);
            }

            return null;
        }

        BotEntity? entity;
        try
        {
            entity = CodeSerializer.Deserialize<BotEntity>(yaml);
        }
        catch (Exception ex)
        {
            if (fileAccessor.Exists(AgentSyncMarkerPath))
            {
                throw new InvalidOperationException(
                    $"CLI settings.mcs.yml is malformed in a cacheless workspace: {ex.Message}.",
                    ex);
            }

            return null;
        }

        if (entity != null && AgentClassifier.DetectAuthoringShape(entity) == AuthoringShape.CliCopilot)
        {
            return new BotDefinition(entity: entity);
        }

        return null;
    }

    /// <summary>
    /// CliAgentSyncSupport / Node Q (D30): positive-allowlist scan for new
    /// user-authored CLI component files. Scans ONLY the <c>.mcs.yml</c> files that
    /// are direct children of the three-layer component folders LspProjection
    /// projects to, so <c>settings.mcs.yml</c>, <c>agent.sync.yaml</c>, and other
    /// <c>.sync.*</c> overlays are excluded by construction (they are never
    /// components). Each discovered file is compiled with the CLI projection shape
    /// so its schema name is derived from the CLI rules, mirroring the writer exactly.
    /// </summary>
    private void ScanForNewCliFiles(
        IFileAccessor fileAccessor,
        DefinitionBase definition,
        List<BotComponentBase> updatedComponents,
        HashSet<string> existingSchemaNames,
        CancellationToken cancellationToken)
    {
        var knownPaths = definition.Components
            .Select(c => _pathResolver.GetComponentPath(c, definition))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var projectionContext = new ProjectionContext(GetSchemaName(definition));

        foreach (var folder in CliComponentFolders)
        {
            foreach (var file in fileAccessor.ListFiles(folder, "*" + McsFileExtension))
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Allowlist: only a direct .mcs.yml child of a component folder.
                if (!IsDirectMcsComponentFile(file, folder) || knownPaths.Contains(file.ToString()))
                {
                    continue;
                }

                string yaml;
                try
                {
                    using var stream = fileAccessor.OpenRead(file);
                    using var sr = new StreamReader(stream, Encoding.UTF8);
                    yaml = sr.ReadToEnd();
                }
                catch (Exception ex)
                {
                    _syncProgress.Report($"CLI new-file scan: '{file}' could not be read: {ex.Message}. Skipping.");
                    continue;
                }

                if (CodeSerializer.Deserialize(yaml, typeof(BotElement), null) is not BotElement element)
                {
                    continue;
                }

                var (component, error) = _fileParser.CompileFile(file, element, projectionContext, AuthoringShape.CliCopilot);
                if (component == null || error != null || existingSchemaNames.Contains(component.SchemaNameString))
                {
                    continue;
                }

                updatedComponents.Add(component);
                existingSchemaNames.Add(component.SchemaNameString);
            }
        }
    }

    /// <summary>Component-body file extension (language-recognized MCS, D28).</summary>
    private const string McsFileExtension = ".mcs.yml";

    /// <summary>
    /// The CLI three-layer component-body folders for the D30 allowlist scan, sourced
    /// from <see cref="LspProjection.CliComponentBodyFolders"/> (derived from the CLI
    /// projection rules) so this scan cannot drift from the projection.
    /// </summary>
    private static string[] CliComponentFolders => LspProjection.CliComponentBodyFolders;

    /// <summary>
    /// True when <paramref name="file"/> is a direct <c>.mcs.yml</c> child of
    /// <paramref name="folder"/> (no nested path segment), the D30 allowlist predicate.
    /// </summary>
    private static bool IsDirectMcsComponentFile(AgentFilePath file, string folder)
    {
        var prefix = folder + "/";
        var pathStr = file.ToString();
        if (!pathStr.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var leaf = pathStr.Substring(prefix.Length);
        if (leaf.IndexOf('/') >= 0 || leaf.IndexOf('\\') >= 0)
        {
            return false;
        }

        return leaf.EndsWith(McsFileExtension, StringComparison.Ordinal);
    }

    /// <summary>
    /// CliAgentSyncSupport / Node Q (old-layout-no-nuke): true when any legacy
    /// bare-body <c>.yaml</c> component file (written by pre-Q code) lingers as a
    /// direct child of a CLI three-layer component folder. Used to detect an
    /// old/transition workspace so the new <c>.mcs.yml</c> reader does not interpret
    /// the absent <c>.mcs.yml</c> files as user deletes (re-clone migrates).
    /// </summary>
    private static bool HasLegacyCliYamlComponentFiles(IFileAccessor fileAccessor)
    {
        foreach (var folder in CliComponentFolders)
        {
            var prefix = folder + "/";
            foreach (var file in fileAccessor.ListFiles(folder, "*.yaml"))
            {
                var pathStr = file.ToString();
                if (!pathStr.StartsWith(prefix, StringComparison.Ordinal))
                {
                    continue;
                }

                var leaf = pathStr.Substring(prefix.Length);
                if (leaf.IndexOf('/') >= 0 || leaf.IndexOf('\\') >= 0)
                {
                    continue;
                }

                // *.yaml does not match .mcs.yml (different extension). Exclude the
                // .sync.yaml overlay family and the rare .mcs.yaml so only genuine
                // legacy bare-body component files count.
                if (leaf.EndsWith(".sync.yaml", StringComparison.Ordinal)
                    || leaf.EndsWith(".mcs.yaml", StringComparison.Ordinal))
                {
                    continue;
                }

                return true;
            }
        }

        return false;
    }

    private async Task<ImmutableArray<EnvironmentVariableDefinition>> ReadEnvironmentVariablesAsync(
        IFileAccessor fileAccessor,
        DefinitionBase definition,
        CancellationToken cancellationToken)
    {
        var envVarFiles = fileAccessor.ListFiles(EnvironmentVariablesFolder, "*.mcs.yml").ToList();
        if (envVarFiles.Count == 0)
        {
            // No env var files on disk — return what's in the cloud cache
            return definition.EnvironmentVariables;
        }

        var result = ImmutableArray.CreateBuilder<EnvironmentVariableDefinition>();

        foreach (var filePath in envVarFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var stream = fileAccessor.OpenRead(filePath);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            // No #if: small env-var file; boundary cancellation is sufficient.
            cancellationToken.ThrowIfCancellationRequested();
            var yaml = await reader.ReadToEndAsync().ConfigureAwait(false);

            if (CodeSerializer.Deserialize(yaml, typeof(EnvironmentVariableDefinition), null) is EnvironmentVariableDefinition envVar)
            {
                // Restore metadata (Id, etc.) from cloud cache if available
                var cached = definition.EnvironmentVariables.FirstOrDefault(
                    e => string.Equals(e.SchemaName.Value, envVar.SchemaName.Value, StringComparison.OrdinalIgnoreCase));
                if (cached != null)
                {
                    var builder = envVar.ToBuilder();
                    builder.Id = cached.Id;
                    result.Add(builder.Build());
                }
                else
                {
                    result.Add(envVar);
                }
            }
        }

        return result.ToImmutable();
    }

    private bool IsValidFileToUpload(IFileAccessor fileAccessor, AgentFilePath knowledgeFile)
    {
        const long maxFileSize = 125L * 1024 * 1024; // 125 MB

        if (!fileAccessor.Exists(knowledgeFile))
        {
            return false;
        }

        long length = 0;
        try
        {
            using var stream = fileAccessor.OpenRead(knowledgeFile);
            length = stream.Length;
        }
        catch (FileNotFoundException)
        {
            return false;
        }

        if (length > maxFileSize)
        {
            _syncProgress.Report($"File '{knowledgeFile.FileName}' exceeded file size limit of 125MB and will be skipped.");
            return false;
        }

        return true;
    }

    private static string GetSchemaName(DefinitionBase definition)
    {
        return definition switch
        {
            BotDefinition botDef when botDef.Entity != null => botDef.Entity.SchemaName.Value,
            BotComponentCollectionDefinition collectionDef when collectionDef.ComponentCollection != null => collectionDef.ComponentCollection.SchemaName.Value,
            _ => string.Empty
        };
    }

    private async Task<ImmutableArray<Change>> GetLocalWorkflowChangesAsync(DirectoryPath workspaceFolder, ISyncDataverseClient dataverseClient, AgentSyncInfo syncInfo, IFileAccessor fileAccessor, DefinitionBase originalDefinition, CancellationToken cancellationToken)
    {
        var localContent = await GetLocalWorkflowContentAsync(workspaceFolder, dataverseClient, syncInfo, fileAccessor, cancellationToken).ConfigureAwait(false);
        var originalContent = await GetOriginalWorkflowContentAsync(originalDefinition, dataverseClient, cancellationToken).ConfigureAwait(false);
        return ComputeWorkflowChanges(originalContent, localContent, isLocal: true);
    }

    private async Task<ImmutableArray<Change>> GetRemoteWorkflowChangesAsync(ISyncDataverseClient dataverseClient, AgentSyncInfo syncInfo, DefinitionBase originalDefinition, CancellationToken cancellationToken)
    {
        var remoteContent = await GetRemoteWorkflowContentAsync(dataverseClient, syncInfo, cancellationToken).ConfigureAwait(false);
        var originalContent = await GetOriginalWorkflowContentAsync(originalDefinition, dataverseClient, cancellationToken).ConfigureAwait(false);
        return ComputeWorkflowChanges(originalContent, remoteContent, isLocal: false);
    }

    private async Task<CloudFlowMetadata> GetLocalWorkflowContentAsync(DirectoryPath workspaceFolder, ISyncDataverseClient dataverseClient, AgentSyncInfo syncInfo, IFileAccessor fileAccessor, CancellationToken cancellationToken)
    {
        var cloudFlowDefinitions = new List<CloudFlowDefinition>();
        var workflows = new List<WorkflowMetadata>();
        var workflowsDir = Path.Combine(workspaceFolder.ToString(), WorkflowFolder);

        if (!Directory.Exists(workflowsDir))
        {
            return new CloudFlowMetadata
            {
                Workflows = ImmutableArray<CloudFlowDefinition>.Empty,
                ConnectionReferences = ImmutableArray<ConnectionReference>.Empty
            };
        }

        foreach (var workflowFolder in Directory.EnumerateDirectories(workflowsDir))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var metadataFile = Path.Combine(workflowFolder, "metadata.yml");
            var jsonFile = Path.Combine(workflowFolder, "workflow.json");

            if (!File.Exists(metadataFile) || !File.Exists(jsonFile))
            {
                continue;
            }

            var yaml = await FileShim.ReadAllTextAsync(metadataFile, cancellationToken).ConfigureAwait(false);
            var json = await FileShim.ReadAllTextAsync(jsonFile, cancellationToken).ConfigureAwait(false);
            var deserializer = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).Build();
            var metadata = deserializer.Deserialize<WorkflowMetadata>(yaml)
                ?? throw new InvalidOperationException($"Workflow metadata file is empty or invalid.");
            metadata.ClientData = json;
            workflows.Add(metadata);
            var (definition, _) = GetFlowDefinition(metadata);
            cloudFlowDefinitions.Add(definition);
        }

        return new CloudFlowMetadata
        {
            Workflows = cloudFlowDefinitions.ToImmutableArray(),
            ConnectionReferences = await GetConnectionReferenceFromLogicalNamesAsync(GetConnectionReferenceLogicalNamesFromFlows(workflows).ToImmutableArray(), dataverseClient, cancellationToken).ConfigureAwait(false)
        };
    }

    private async Task<CloudFlowMetadata> GetRemoteWorkflowContentAsync(ISyncDataverseClient dataverseClient, AgentSyncInfo syncInfo, CancellationToken cancellationToken)
    {
        var cloudFlowDefinitions = new List<CloudFlowDefinition>();
        var workflows = new List<WorkflowMetadata>();
        var remote = await dataverseClient.DownloadAllWorkflowsForAgentAsync(syncInfo, cancellationToken).ConfigureAwait(false);

        if (remote == null || remote.Length == 0)
        {
            return new CloudFlowMetadata
            {
                Workflows = ImmutableArray<CloudFlowDefinition>.Empty,
                ConnectionReferences = ImmutableArray<ConnectionReference>.Empty
            };
        }

        foreach (var workflow in remote)
        {
            var (definition, _) = GetFlowDefinition(workflow);
            cloudFlowDefinitions.Add(definition);
            workflows.Add(workflow);
        }

        return new CloudFlowMetadata
        {
            Workflows = cloudFlowDefinitions.ToImmutableArray(),
            ConnectionReferences = await GetConnectionReferenceFromLogicalNamesAsync(GetConnectionReferenceLogicalNamesFromFlows(workflows).ToImmutableArray(), dataverseClient, cancellationToken).ConfigureAwait(false)
        };
    }

    private async Task<CloudFlowMetadata> GetOriginalWorkflowContentAsync(DefinitionBase definitionBase, ISyncDataverseClient dataverseClient, CancellationToken cancellationToken)
    {
        if (definitionBase?.Flows == null)
        {
            return new CloudFlowMetadata
            {
                Workflows = ImmutableArray<CloudFlowDefinition>.Empty,
                ConnectionReferences = ImmutableArray<ConnectionReference>.Empty
            };
        }

        var workflows = definitionBase.Flows;
        var allLogicalNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var flow in workflows)
        {
            var clientData = GetClientData(flow);
            if (!string.IsNullOrWhiteSpace(clientData))
            {
                using var doc = JsonDocument.Parse(clientData);
                var names = ExtractConnectionReferenceLogicalNames(doc.RootElement);

                foreach (var name in names)
                {
                    allLogicalNames.Add(name);
                }
            }
        }

        return new CloudFlowMetadata
        {
            Workflows = workflows,
            ConnectionReferences = await GetConnectionReferenceFromLogicalNamesAsync(allLogicalNames.ToImmutableArray(), dataverseClient, cancellationToken).ConfigureAwait(false)
        };
    }

    private static ImmutableArray<Change> ComputeWorkflowChanges(CloudFlowMetadata original, CloudFlowMetadata current, bool isLocal)
    {
        var changes = ImmutableArray.CreateBuilder<Change>();
        var originalMap = original.Workflows.ToDictionary(x => x.WorkflowId.Value);
        var currentMap = current.Workflows.ToDictionary(x => x.WorkflowId.Value);

        foreach (var kvp in currentMap)
        {
            var workflowId = kvp.Key;
            var workflow = kvp.Value;
            var (workflowJsonPath, workflowMetadataPath) = GetWorkflowPath(workflow.DisplayName, workflowId);
            var name = workflow.DisplayName ?? workflowId.ToString();
            var remoteClientData = !isLocal ? GetClientData(workflow) : null;
            var remoteMetadata = !isLocal ? GetWorkflowMetadata(workflow) : null;

            if (!originalMap.ContainsKey(workflowId))
            {
                changes.Add(new Change
                {
                    Name = name,
                    Uri = workflowJsonPath.ToString(),
                    ChangeType = ChangeType.Create,
                    ChangeKind = BotElementKind.CloudFlowDefinition.ToString(),
                    SchemaName = $"Mcs.Workflow.{workflowId}",
                    RemoteWorkflowContent = remoteClientData
                });

                changes.Add(new Change
                {
                    Name = name,
                    Uri = workflowMetadataPath.ToString(),
                    ChangeType = ChangeType.Create,
                    ChangeKind = BotElementKind.CloudFlowDefinition.ToString(),
                    SchemaName = $"Mcs.Workflow.{workflowId}.metadata",
                    RemoteWorkflowContent = remoteMetadata
                });
            }
            else
            {
                var originalWorkflow = originalMap[workflowId];
                var clientChanged = GetClientData(workflow) != GetClientData(originalWorkflow);
                var metadataChanged = GetWorkflowMetadata(workflow) != GetWorkflowMetadata(originalWorkflow);

                if (clientChanged)
                {
                    changes.Add(new Change
                    {
                        Name = name,
                        Uri = workflowJsonPath.ToString(),
                        ChangeType = ChangeType.Update,
                        ChangeKind = BotElementKind.CloudFlowDefinition.ToString(),
                        SchemaName = $"Mcs.Workflow.{workflowId}",
                        RemoteWorkflowContent = remoteClientData
                    });
                }

                if (metadataChanged)
                {
                    changes.Add(new Change
                    {
                        Name = name,
                        Uri = workflowMetadataPath.ToString(),
                        ChangeType = ChangeType.Update,
                        ChangeKind = BotElementKind.CloudFlowDefinition.ToString(),
                        SchemaName = $"Mcs.Workflow.{workflowId}.metadata",
                        RemoteWorkflowContent = remoteMetadata
                    });
                }
            }
        }

        foreach (var kvp in originalMap)
        {
            var workflowId = kvp.Key;
            var workflow = kvp.Value;

            if (!currentMap.ContainsKey(workflowId))
            {
                changes.Add(new Change
                {
                    Name = workflow.DisplayName ?? workflowId.ToString(),
                    Uri = workflowId.ToString(),
                    ChangeType = ChangeType.Delete,
                    ChangeKind = BotElementKind.CloudFlowDefinition.ToString(),
                    SchemaName = $"Mcs.Workflow.{workflowId}"
                });
            }
        }

        return changes.ToImmutable();
    }

    private static (AgentFilePath, AgentFilePath) GetWorkflowPath(string? workflowName, Guid workflowId)
    {
        var folderName = $"{new string(((workflowName ?? string.Empty)).Where(c => !Path.GetInvalidFileNameChars().Contains(c) && !char.IsWhiteSpace(c)).ToArray()).TrimEnd('.', ' ')}-{workflowId}";
        var workflowFolder = Path.Combine(WorkflowFolder, folderName).Replace("\\", "/");
        var workflowJson = new AgentFilePath($"{workflowFolder}/workflow.json");
        var workflowMetadata = new AgentFilePath($"{workflowFolder}/metadata.yml");
        return (workflowJson, workflowMetadata);
    }

    private async Task<ImmutableArray<ConnectionReference>> GetConnectionReferenceFromLogicalNamesAsync(IEnumerable<string> logicalNames, ISyncDataverseClient dataverseClient, CancellationToken cancellationToken)
    {
        var references = ImmutableArray<ConnectionReference>.Empty;

        if (logicalNames.Any())
        {
            var result = await dataverseClient.GetConnectionReferencesByLogicalNamesAsync(logicalNames, cancellationToken).ConfigureAwait(false);
            references = result.Select(dto => new ConnectionReference(connectionReferenceLogicalName: dto.ConnectionReferenceLogicalName, connectionId: dto.ConnectionId, connectorId: dto.ConnectorId ?? throw new InvalidOperationException($"ConnectorId missing for connection reference {dto.ConnectionReferenceLogicalName}"))).ToImmutableArray();
        }

        return references;
    }

    private static ImmutableArray<string> GetConnectionReferenceLogicalNamesFromFlows(IEnumerable<WorkflowMetadata> workflows)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var workflow in workflows)
        {
            if (string.IsNullOrWhiteSpace(workflow.ClientData))
            {
                continue;
            }

            // ns2.0 BCL's IsNullOrWhiteSpace lacks NotNullWhen; ! is compile-time only.
            using var doc = JsonDocument.Parse(workflow.ClientData!);
            var root = doc.RootElement;
            var names = ExtractConnectionReferenceLogicalNames(root);

            foreach (var name in names)
            {
                set.Add(name);
            }
        }

        return set.ToImmutableArray();
    }

    private static string GetClientData(CloudFlowDefinition? flow)
    {
        if (flow?.ExtensionData?.Properties.TryGetValue("clientdata", out var value) == true && value is StringDataValue s && !string.IsNullOrEmpty(s.Value))
        {
            try
            {
                // ns2.0 BCL's IsNullOrEmpty lacks NotNullWhen; ! is compile-time only.
                using var doc = JsonDocument.Parse(s.Value!);
                return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
            }
            catch (JsonException)
            {
                return s.Value!;
            }
        }

        return string.Empty;
    }

    private static string NormalizeWorkflowClientData(string? clientData)
    {
        if (string.IsNullOrWhiteSpace(clientData))
        {
            return string.Empty;
        }

        try
        {
            using var doc = JsonDocument.Parse(clientData!);
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException)
        {
            return clientData!;
        }
    }

    private static string GetWorkflowMetadata(CloudFlowDefinition? flow)
    {
        if (flow?.ExtensionData?.Properties.TryGetValue("metadata", out var value) == true && value is StringDataValue s && !string.IsNullOrEmpty(s.Value))
        {
            return s.Value!;
        }

        return string.Empty;
    }

    private static string NormalizeWorkflowMetadata(WorkflowMetadata workflow)
    {
        var savedJsonFileName = workflow.JsonFileName;
        try
        {
            var (workflowJsonPath, _) = GetWorkflowPath(workflow.Name, workflow.WorkflowId);
            workflow.JsonFileName = workflowJsonPath.ToString();
            var serializer = new SerializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).Build();
            return serializer.Serialize(workflow);
        }
        finally
        {
            workflow.JsonFileName = savedJsonFileName;
        }
    }

    private static async Task<bool> WorkflowFileNeedsWriteAsync(IFileAccessor fileAccessor, AgentFilePath path, string newContent, CancellationToken cancellationToken)
    {
        if (!fileAccessor.Exists(path))
        {
            return true;
        }

        var existingContent = await fileAccessor.ReadStringAsync(path, cancellationToken).ConfigureAwait(false);
        return !string.Equals(existingContent, newContent, StringComparison.Ordinal);
    }

    private static (CloudFlowDefinition, ImmutableArray<string>) GetFlowDefinition(WorkflowMetadata workflow)
    {
        RecordDataType? inputType = null;
        RecordDataType? outputType = null;
        var workflowConnectionNames = ImmutableArray<string>.Empty;
        if (!string.IsNullOrWhiteSpace(workflow.ClientData))
        {
            // ns2.0 BCL's IsNullOrWhiteSpace lacks NotNullWhen; ! is compile-time only.
            using var document = JsonDocument.Parse(workflow.ClientData!);
            var root = document.RootElement;
            inputType = ExtractRecordDataType(
                root,
                "properties",
                "definition",
                "triggers",
                "manual",
                "inputs",
                "schema",
                "properties"
            );

            outputType = ExtractWorkflowOutputType(root);
            workflowConnectionNames = ExtractConnectionReferenceLogicalNames(root);
        }

        var cloudFlowDefinition = new CloudFlowDefinition(
            displayName: workflow.Name,
            isEnabled: true,
            workflowId: new FlowId(workflow.WorkflowId),
            inputType: inputType,
            outputType: outputType,
            extensionData: new RecordDataValue(BuildFlowExtensionData(workflow))
        );

        return (cloudFlowDefinition, workflowConnectionNames);
    }

    private static ImmutableDictionary<string, DataValue> BuildFlowExtensionData(WorkflowMetadata workflow)
    {
        var properties = ImmutableDictionary<string, DataValue>.Empty.Add("metadata", DataValue.Create(NormalizeWorkflowMetadata(workflow)));

        if (!string.IsNullOrWhiteSpace(workflow.ClientData))
        {
            properties = properties.Add("clientdata", DataValue.Create(workflow.ClientData!));
        }

        return properties;
    }

    private static ImmutableArray<string> ExtractConnectionReferenceLogicalNames(JsonElement root)
    {
        if (!root.TryGetProperty("properties", out var propertiesElement))
        {
            return ImmutableArray<string>.Empty;
        }

        if (!propertiesElement.TryGetProperty("connectionReferences", out var connectionsElement))
        {
            return ImmutableArray<string>.Empty;
        }

        if (connectionsElement.ValueKind != JsonValueKind.Object)
        {
            return ImmutableArray<string>.Empty;
        }

        var builder = ImmutableArray.CreateBuilder<string>();

        foreach (var connection in connectionsElement.EnumerateObject())
        {
            var value = connection.Value;

            if (!value.TryGetProperty("connection", out var connectionObj))
            {
                continue;
            }

            if (!connectionObj.TryGetProperty("connectionReferenceLogicalName", out var logicalNameElement))
            {
                continue;
            }

            var logicalName = logicalNameElement.GetString();

            if (!string.IsNullOrWhiteSpace(logicalName))
            {
                builder.Add(logicalName);
            }
        }

        return builder.ToImmutable();
    }

    // Match any GUID at the end of the string
    private static Guid? ExtractWorkflowIdFromFileName(string fileName)
    {
        var match = System.Text.RegularExpressions.Regex.Match(fileName, @"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})$");
        if (match.Success && Guid.TryParse(match.Value, out var workflowId))
        {
            return workflowId;
        }

        return null;
    }

    private static RecordDataType? ExtractRecordDataType(JsonElement root, params string[] propertyPath)
    {
        var current = root;
        foreach (var prop in propertyPath)
        {
            if (!current.TryGetProperty(prop, out current))
            {
                return null;
            }
        }

        var dict = new Dictionary<string, PropertyInfo>();
        foreach (var prop in current.EnumerateObject())
        {
            dict[prop.Name] = CreatePropertyInfoFromJson(prop.Value, prop.Name);
        }

        return dict.Count == 0 ? null : new RecordDataType(dict.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase));
    }

    private static RecordDataType? ExtractWorkflowOutputType(JsonElement root)
    {
        if (!root.TryGetProperty("properties", out var props) || !props.TryGetProperty("definition", out var definition) || !definition.TryGetProperty("actions", out var actions))
        {
            return null;
        }

        var properties = new Dictionary<string, PropertyInfo>(StringComparer.OrdinalIgnoreCase);
        CollectResponseOutputs(actions, properties);

        return properties.Count == 0 ? null : new RecordDataType(properties.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase));
    }

    private static void CollectResponseOutputs(JsonElement actions, Dictionary<string, PropertyInfo> properties)
    {
        if (actions.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var actionProperty in actions.EnumerateObject())
        {
            var actionValue = actionProperty.Value;
            if (actionValue.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var isResponse = actionValue.TryGetProperty("type", out var typeNode) && string.Equals(typeNode.GetString(), "Response", StringComparison.OrdinalIgnoreCase);

            if (isResponse && actionValue.TryGetProperty("inputs", out var inputs) && inputs.TryGetProperty("schema", out var schema) && schema.TryGetProperty("properties", out var outputProps) && outputProps.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in outputProps.EnumerateObject())
                {
                    if (!properties.ContainsKey(prop.Name))
                    {
                        properties[prop.Name] = CreatePropertyInfoFromJson(prop.Value, prop.Name);
                    }
                }
            }

            // Recurse into nested actions
            if (actionValue.TryGetProperty("actions", out var nestedActions))
            {
                CollectResponseOutputs(nestedActions, properties);
            }

            if (actionValue.TryGetProperty("else", out var elseBranch) && elseBranch.TryGetProperty("actions", out var elseActions))
            {
                CollectResponseOutputs(elseActions, properties);
            }

            if (actionValue.TryGetProperty("cases", out var cases) && cases.ValueKind == JsonValueKind.Object)
            {
                foreach (var caseProperty in cases.EnumerateObject())
                {
                    if (caseProperty.Value.ValueKind == JsonValueKind.Object && caseProperty.Value.TryGetProperty("actions", out var caseActions))
                    {
                        CollectResponseOutputs(caseActions, properties);
                    }
                }
            }

            if (actionValue.TryGetProperty("default", out var defaultBranch) && defaultBranch.TryGetProperty("actions", out var defaultActions))
            {
                CollectResponseOutputs(defaultActions, properties);
            }
        }
    }

    private static PropertyInfo CreatePropertyInfoFromJson(JsonElement propValue, string propName)
    {
        DataType type = DataType.String;

        var schemaType = propValue.TryGetProperty("type", out var typeNode) ? typeNode.GetString() : null;

        if (schemaType != null)
        {
            type = MapFlowType(schemaType);
        }

        if (string.Equals(schemaType, "object", StringComparison.OrdinalIgnoreCase) && propValue.TryGetProperty("properties", out var nestedProps) && nestedProps.ValueKind == JsonValueKind.Object)
        {
            var fields = new Dictionary<string, PropertyInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var nested in nestedProps.EnumerateObject())
            {
                fields[nested.Name] = CreatePropertyInfoFromJson(nested.Value, nested.Name);
            }

            type = fields.Count == 0 ? DataType.EmptyRecord : new RecordDataType(fields.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase));
        }
        else if (propValue.TryGetProperty("x-ms-content-hint", out var hintNode) && string.Equals(hintNode.GetString(), "FILE", StringComparison.OrdinalIgnoreCase))
        {
            type = DataType.File;
        }

        var property = new PropertyInfo(
            displayName: propValue.TryGetProperty("title", out var titleNode) ? titleNode.GetString() : propName,
            description: propValue.TryGetProperty("description", out var descNode) ? descNode.GetString() : null,
            isRequired: false,
            type: type
        );

        return property;
    }

    private static DataType MapFlowType(string jsonType)
    {
        return jsonType.ToUpperInvariant() switch
        {
            "STRING" => DataType.String,
            "BOOLEAN" => DataType.Boolean,
            "NUMBER" => DataType.Number,
            "INTEGER" => DataType.Number,
            "DATE" => DataType.DateTime,
            _ => DataType.String
        };
    }

    /// <summary>
    /// Validates connection references - removes orphaned entries without workspace usage (after pull).
    /// </summary>
    private BotDefinition? ValidateConnectionReferences(DefinitionBase definition)
    {
        if (definition is not BotDefinition bot || bot.Entity == null)
        {
            return null;
        }

        // Get set of all ConnectionReferenceLogicalNames actually used in workspace
        var usedConnections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Collect all connection reference usages from the definition
        foreach (var connRef in definition.ConnectionReferences)
        {
            var name = connRef.ConnectionReferenceLogicalName.ToString();
            if (!string.IsNullOrEmpty(name))
            {
                usedConnections.Add(name);
            }
        }

        // Filter definition.ConnectionReferences to only include those actually used
        var validConnections = definition.ConnectionReferences
            .Where(cr => usedConnections.Contains(cr.ConnectionReferenceLogicalName.ToString()))
            .OrderBy(cr => cr.ConnectionReferenceLogicalName.ToString(), StringComparer.Ordinal)
            .ToList();

        if (validConnections.Count < definition.ConnectionReferences.Length)
        {
            var removed = definition.ConnectionReferences.Length - validConnections.Count;
            _syncProgress.Report($"Removed {removed} orphaned connection reference(s) with no workspace usage");
        }

        return bot;
    }

    /// <summary>
    /// Merges connection references from updated definition into changeset.
    /// This preserves cloud's authoritative data (version, IDs, etc.).
    /// </summary>
    private PvaComponentChangeSet MergeConnectionReferencesIntoChangeset(DefinitionBase updatedDefinition, PvaComponentChangeSet changeset)
    {
        // Connection references are already in the definition, no need to merge into entity
        return changeset;
    }

    private Task WriteConnectionReferencesAsync(IFileAccessor fileAccessor, DefinitionBase definition, bool isCliAgent, CancellationToken cancellationToken)
    {
        var uniqueConnectionReferences = definition.ConnectionReferences
                .Where(cr => !string.IsNullOrEmpty(cr.ConnectionReferenceLogicalName.Value))
                .GroupBy(cr => cr.ConnectionReferenceLogicalName.Value, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.Last())
                .ToList();

        if (isCliAgent)
        {
            // CliAgentSyncSupport / Node D5 + TDD D33: per-reference layered shape at
            // infrastructure/connections/{logicalName}.sync.yaml, suppressing the flat
            // connectionreferences.mcs.yml. The per-file set is written+committed FIRST,
            // and only then is any stale flat file (left over from a prior classic-shape
            // clone) removed. This ordering makes a cancellation/IO failure recoverable:
            // the flat file stays intact until the per-file set is complete, so an
            // interruption never leaves BOTH gone (which would make the next push read zero
            // connection references on disk and synthesize a delete for every cloud ref).
            // The writer pre-prunes infrastructure/connections/ for orphan removal, so this
            // branch is correct even on the 0-reference case.
            CliAgentConnectionsWriter.WriteAll(
                fileAccessor,
                uniqueConnectionReferences,
                _syncProgress.Report,
                cancellationToken);

            if (fileAccessor.Exists(ConnectionReferencesPath))
            {
                fileAccessor.Delete(ConnectionReferencesPath);
            }

            return Task.CompletedTask;
        }

        // Classic kind: unchanged flat file at workspace root.
        if (uniqueConnectionReferences.Any())
        {
            using var file = fileAccessor.OpenWrite(ConnectionReferencesPath);
            using var sw = new StreamWriter(file, Encoding.UTF8);
            using var yamlContext = YamlSerializationContext.UseStandardSerializationContextIfNotDefined(throwOnInvalidYaml: false);

            CodeSerializer.SerializeConnectionReferences(sw, uniqueConnectionReferences);
        }

        return Task.CompletedTask;
    }

    private async Task WriteCustomConnectorsAsync(IFileAccessor fileAccessor, DirectoryPath workspaceFolder, DefinitionBase definition, ISyncDataverseClient dataverseClient, CancellationToken cancellationToken)
    {
        var expectedConnectorIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var internalIdToConnectorId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!definition.ConnectionReferences.IsDefaultOrEmpty)
        {
            foreach (var cr in definition.ConnectionReferences)
            {
                var internalId = SyncDataverseClient.ExtractConnectorInternalId(cr.ConnectorId.ToString());
                if (string.IsNullOrEmpty(internalId))
                {
                    continue;
                }

                internalIdToConnectorId[internalId!] = cr.ConnectorId.ToString();
            }
        }

        var connectorsRoot = Path.Combine(workspaceFolder.ToString(), ConnectorsFolder);
        if (Directory.Exists(connectorsRoot))
        {
            foreach (var existing in Directory.EnumerateDirectories(connectorsRoot))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var folderName = Path.GetFileName(existing);
                var guid = ExtractTrailingGuid(folderName);

                if (guid == null)
                {
                    continue;
                }

                var stillReferenced = internalIdToConnectorId.Values.Any(v => Guid.TryParse(v, out var g) && g == guid.Value);
                if (!stillReferenced)
                {
                    try
                    {
                        Directory.Delete(existing, recursive: true);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        _syncProgress.Report($"Failed to remove orphaned connector folder '{folderName}': {ex.Message}");
                    }
                }
            }
        }

        if (internalIdToConnectorId.Count == 0)
        {
            return;
        }

        CustomConnectorMetadata[] connectors;
        try
        {
            connectors = await dataverseClient.DownloadConnectorsByInternalIdsAsync(internalIdToConnectorId.Keys, isManaged: false, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _syncProgress.Report($"Failed to download custom connectors: {ex.Message}");
            return;
        }

        var returnedConnectorIds = new HashSet<Guid>(connectors.Select(c => c.ConnectorId));
        var requestedConnectorIds = new HashSet<Guid>();
        foreach (var idString in internalIdToConnectorId.Values)
        {
            if (Guid.TryParse(idString, out var g))
            {
                requestedConnectorIds.Add(g);
            }
        }

        var connectorsRootForReconcile = Path.Combine(workspaceFolder.ToString(), ConnectorsFolder);
        if (Directory.Exists(connectorsRootForReconcile))
        {
            foreach (var missingId in requestedConnectorIds.Where(id => !returnedConnectorIds.Contains(id)))
            {
                foreach (var existing in Directory.EnumerateDirectories(connectorsRootForReconcile))
                {
                    var folderName = Path.GetFileName(existing);
                    if (ExtractTrailingGuid(folderName) != missingId)
                    {
                        continue;
                    }

                    try
                    {
                        Directory.Delete(existing, recursive: true);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        _syncProgress.Report($"Failed to remove deleted connector folder '{folderName}': {ex.Message}");
                    }
                }
            }
        }

        if (connectors.Length == 0)
        {
            return;
        }

        var jsonOpts = new JsonSerializerOptions { WriteIndented = true };

        foreach (var connector in connectors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var nameSeed = !string.IsNullOrWhiteSpace(connector.Name) ? connector.Name! : (!string.IsNullOrWhiteSpace(connector.ConnectorInternalId) ? connector.ConnectorInternalId! : connector.ConnectorId.ToString());
            var safeName = SanitizeConnectorFolderName(nameSeed);
            var folder = $"{ConnectorsFolder}/{safeName}-{connector.ConnectorId}";
            var swaggerWritten = false;

            if (!string.IsNullOrWhiteSpace(connector.OpenApiDefinition))
            {
                var swaggerPath = new AgentFilePath($"{folder}/openapidefinition.json");
                string pretty;
                try
                {
                    using var doc = JsonDocument.Parse(connector.OpenApiDefinition!);
                    pretty = JsonSerializer.Serialize(doc.RootElement, jsonOpts);
                }
                catch (JsonException)
                {
                    pretty = connector.OpenApiDefinition!;
                }

                await fileAccessor.WriteAsync(swaggerPath, pretty, cancellationToken).ConfigureAwait(false);
                swaggerWritten = true;
            }

            var connParamsJson = string.IsNullOrWhiteSpace(connector.ConnectionParameters) ? "{}" : connector.ConnectionParameters!;
            string connParamsPretty;
            try
            {
                using var doc = JsonDocument.Parse(connParamsJson);
                connParamsPretty = JsonSerializer.Serialize(doc.RootElement, jsonOpts);
            }
            catch (JsonException)
            {
                connParamsPretty = connParamsJson;
            }

            await fileAccessor.WriteAsync(new AgentFilePath($"{folder}/connectionparameters.json"), connParamsPretty, cancellationToken).ConfigureAwait(false);

            var policyJson = string.IsNullOrWhiteSpace(connector.PolicyTemplateInstances) ? "{}" : connector.PolicyTemplateInstances!;
            string policyPretty;
            try
            {
                using var doc = JsonDocument.Parse(policyJson);
                policyPretty = JsonSerializer.Serialize(doc.RootElement, jsonOpts);
            }
            catch (JsonException)
            {
                policyPretty = policyJson;
            }

            await fileAccessor.WriteAsync(new AgentFilePath($"{folder}/policytemplateinstances.json"), policyPretty, cancellationToken).ConfigureAwait(false);
            var iconWritten = false;

            if (!string.IsNullOrWhiteSpace(connector.IconBlobBase64))
            {
                try
                {
                    var iconBytes = Convert.FromBase64String(connector.IconBlobBase64!);
                    await fileAccessor.WriteAsync(new AgentFilePath($"{folder}/iconblob.png"), iconBytes, cancellationToken).ConfigureAwait(false);
                    iconWritten = true;
                }
                catch (FormatException)
                {
                    _syncProgress.Report($"Connector '{connector.ConnectorInternalId}': icon base64 decode failed; skipping iconblob.png");
                }
            }

            var metadataNode = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(connector, jsonOpts))!.AsObject();
            var connectorNameForRef = !string.IsNullOrWhiteSpace(connector.Name) ? connector.Name! : safeName;
            metadataNode["openapidefinition"] = swaggerWritten ? $"{folder}/openapidefinition.json" : null;
            metadataNode["connectionparameters"] = $"{folder}/connectionparameters.json";
            metadataNode["policytemplateinstances"] = $"{folder}/policytemplateinstances.json";
            metadataNode["iconblob"] = iconWritten ? $"{folder}/iconblob.png" : null;

            await fileAccessor.WriteAsync(new AgentFilePath($"{folder}/metadata.yml"), metadataNode.ToJsonString(jsonOpts), cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<CustomConnectorPushResult> PushCustomConnectorsAsync(DirectoryPath workspaceFolder, ISyncDataverseClient dataverseClient, CancellationToken cancellationToken)
    {
        var pushedRowIds = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var connectorsRoot = Path.Combine(workspaceFolder.ToString(), ConnectorsFolder);
        if (!Directory.Exists(connectorsRoot))
        {
            return new CustomConnectorPushResult { PushedRowIds = pushedRowIds };
        }

        var localConnectors = new List<CustomConnectorMetadata>();

        foreach (var folder in Directory.EnumerateDirectories(connectorsRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folderName = Path.GetFileName(folder);
            var local = await TryLoadLocalConnectorAsync(folder, cancellationToken).ConfigureAwait(false);
            if (local == null)
            {
                continue;
            }

            if (local.ConnectorId == Guid.Empty)
            {
                var guid = ExtractTrailingGuid(folderName);
                if (guid.HasValue)
                {
                    local.ConnectorId = guid.Value;
                }
                else
                {
                    continue;
                }
            }

            localConnectors.Add(local);
        }

        if (localConnectors.Count == 0)
        {
            return new CustomConnectorPushResult { PushedRowIds = pushedRowIds };
        }

        foreach (var local in localConnectors)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await dataverseClient.UpsertConnectorAsync(local, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(local.ConnectorInternalId))
                {
                    pushedRowIds[local.ConnectorInternalId!] = local.ConnectorId;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _syncProgress.Report($"Failed to push custom connector '{local.Name ?? local.ConnectorId.ToString()}': {ex.Message}");
                // continue with other connector
            }
        }

        return new CustomConnectorPushResult { PushedRowIds = pushedRowIds };
    }

    private static async Task<CustomConnectorMetadata?> TryLoadLocalConnectorAsync(string folder, CancellationToken cancellationToken)
    {
        var metadataPath = Path.Combine(folder, "metadata.yml");
        if (!File.Exists(metadataPath))
        {
            return null;
        }

        string metadataText;
        try
        {
            metadataText = await FileShim.ReadAllTextAsync(metadataPath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return null;
        }

        CustomConnectorMetadata? meta;
        try
        {
            meta = JsonSerializer.Deserialize<CustomConnectorMetadata>(metadataText, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            return null;
        }

        if (meta == null)
        {
            return null;
        }

        meta.OpenApiDefinition = await ReadConnectorMetadataAsync(folder, meta.OpenApiDefinition, cancellationToken).ConfigureAwait(false);
        meta.ConnectionParameters = await ReadConnectorMetadataAsync(folder, meta.ConnectionParameters, cancellationToken).ConfigureAwait(false);
        meta.PolicyTemplateInstances = await ReadConnectorMetadataAsync(folder, meta.PolicyTemplateInstances, cancellationToken).ConfigureAwait(false);
        meta.IconBlobBase64 = await ReadConnectorIconAsync(folder, meta.IconBlobBase64, cancellationToken).ConfigureAwait(false);

        return meta;
    }

    private static async Task<string?> ReadConnectorMetadataAsync(string connectorFolder, string? value, CancellationToken cancellationToken)
    {
        var fullPath = TryResolveConnectorRelativePath(connectorFolder, value);
        if (fullPath == null)
        {
            return value;
        }

        return await FileShim.ReadAllTextAsync(fullPath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string?> ReadConnectorIconAsync(string connectorFolder, string? value, CancellationToken cancellationToken)
    {
        var fullPath = TryResolveConnectorRelativePath(connectorFolder, value);
        if (fullPath == null)
        {
            return value;
        }

#if NETSTANDARD2_0
        var bytes = File.ReadAllBytes(fullPath);
        await Task.CompletedTask.ConfigureAwait(false);
#else
        var bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
#endif
        return Convert.ToBase64String(bytes);
    }

    private static string? TryResolveConnectorRelativePath(string connectorFolder, string? value)
    {
        var workspaceRoot = Path.GetDirectoryName(Path.GetDirectoryName(connectorFolder));
        if (string.IsNullOrWhiteSpace(value) || !value!.StartsWith($"{ConnectorsFolder}/", StringComparison.OrdinalIgnoreCase) || workspaceRoot == null)
        {
            return null;
        }

        string fullPath;
        string connectorRoot;
        try
        {
            fullPath = Path.GetFullPath(Path.Combine(workspaceRoot, value.Replace('/', Path.DirectorySeparatorChar)));
            connectorRoot = Path.GetFullPath(connectorFolder);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        if (!connectorRoot.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
        {
            connectorRoot += Path.DirectorySeparatorChar;
        }

        if (!fullPath.StartsWith(connectorRoot, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return File.Exists(fullPath) ? fullPath : null;
    }

    private static string SanitizeConnectorFolderName(string raw)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (char.IsWhiteSpace(ch) || Array.IndexOf(invalid, ch) >= 0)
            {
                sb.Append('_');
            }
            else
            {
                sb.Append(ch);
            }
        }
        var trimmed = sb.ToString().Trim('.', '_', ' ');
        return string.IsNullOrEmpty(trimmed) ? "connector" : trimmed;
    }

    private static Guid? ExtractTrailingGuid(string folderName)
    {
        var match = System.Text.RegularExpressions.Regex.Match(folderName, @"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})$");
        if (match.Success && Guid.TryParse(match.Value, out var id))
        {
            return id;
        }

        return null;
    }

    public async Task<ImmutableArray<DirectoryPath>> CloneAllAssetsAsync(
        DirectoryPath rootFolder,
        AgentSyncInfo syncInfo,
        AssetsToClone assetsToClone,
        AgentInfo agentInfo,
        IOperationContextProvider operationContextProvider,
        ISyncDataverseClient dataverseClient,
        CancellationToken cancellationToken)
    {
        var referenceTracker = new ReferenceTracker();
        var allOperations = await operationContextProvider.GetAllAsync(syncInfo, assetsToClone).ConfigureAwait(false);
        var workspaceFolders = ImmutableArray.CreateBuilder<DirectoryPath>();

        foreach (var operationContext in allOperations)
        {
            string displayName;
            switch (operationContext)
            {
                case BotComponentCollectionAuthoringOperationContext cc:
                    var collectionInfo = agentInfo.ComponentCollections.FirstOrDefault(c => c.Id == cc.BotComponentCollectionReference.CdsId);
                    displayName = collectionInfo?.DisplayName ?? cc.BotComponentCollectionReference.CdsId.ToString();
                    break;
                case AuthoringOperationContext:
                    displayName = agentInfo.DisplayName;
                    break;
                default:
                    throw new InvalidOperationException($"Unknown operation context type {operationContext.GetType()}.");
            }

            var folderName = SanitizeFolderName(displayName);
            if (string.IsNullOrWhiteSpace(folderName))
            {
                throw new InvalidOperationException($"Display name '{displayName}' is not valid for a folder name.");
            }

            var folder = rootFolder.GetChildDirectoryPath(folderName);
            var folderPath = folder.ToString();
            if (Directory.Exists(folderPath) && Directory.GetFiles(folderPath).Length > 0)
            {
                throw new InvalidOperationException($"Destination path '{folder}' already exists and is not an empty directory.");
            }

            workspaceFolders.Add(folder);

            await SaveSyncInfoAsync(folder, syncInfo).ConfigureAwait(false);
            await CloneChangesAsync(folder, referenceTracker, operationContext, dataverseClient, syncInfo, cancellationToken).ConfigureAwait(false);
        }

        // Second pass: fill in cross-workspace reference paths now that all workspaces exist.
        foreach (var folder in workspaceFolders)
        {
            await ApplyTouchupsAsync(folder, referenceTracker, cancellationToken).ConfigureAwait(false);
        }

        return workspaceFolders.ToImmutable();
    }

    /// <summary>
    /// Sanitizes a display name for use as a folder name.
    /// Keeps alphanumeric, underscore, hyphen, space, and Unicode above 128.
    /// Percent-encodes other ASCII characters.
    /// Ported from CloneAgentHandler.SanitizeFolderName in the extension.
    /// </summary>
    internal static string SanitizeFolderName(string displayName)
    {
        displayName = displayName.Trim();

        bool hasValidCharacters = displayName.Any(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' || c > 128);
        if (!hasValidCharacters) return string.Empty;

        return System.Text.RegularExpressions.Regex.Replace(displayName, @"[\u0000-\u007F]", match =>
        {
            char c = match.Value[0];
            if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == ' ')
                return c.ToString();
            else
                return "%" + ((int)c).ToString("x2");
        });
    }

    public async Task<PushVerificationResult> VerifyPushAsync(
        DirectoryPath workspaceFolder,
        AuthoringOperationContextBase operationContext,
        ISyncDataverseClient dataverseClient,
        AgentSyncInfo syncInfo,
        CancellationToken cancellationToken)
    {
        // Read the pushed (expected) workspace definition
        var expectedDefinition = await ReadWorkspaceDefinitionAsync(workspaceFolder, cancellationToken).ConfigureAwait(false);

        // Clone to a temp workspace to get the server's current state
        var tempDir = Path.Combine(Path.GetTempPath(), "mcs-verify-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(tempDir);
        var tempWorkspace = new DirectoryPath(tempDir.Replace('\\', '/'));

        try
        {
            var referenceTracker = new ReferenceTracker();
            await CloneChangesAsync(tempWorkspace, referenceTracker, operationContext, dataverseClient, syncInfo, cancellationToken).ConfigureAwait(false);

            var serverDefinition = await ReadWorkspaceDefinitionAsync(tempWorkspace, cancellationToken).ConfigureAwait(false);

            // Compare per-entity-type: group expected changes by ChangeKind, count matches in server state
            var (_, expectedChanges) = await GetLocalChangesAsync(tempWorkspace, expectedDefinition, dataverseClient, syncInfo, cancellationToken).ConfigureAwait(false);

            // If there are no local differences between pushed state and server state, everything was accepted
            if (expectedChanges.IsEmpty)
            {
                return new PushVerificationResult { IsFullyAccepted = true };
            }

            // Group differences by change kind to produce per-entity-type results
            var changesByKind = expectedChanges.GroupBy(c => c.ChangeKind);
            var entityTypes = changesByKind.Select(g => new EntityTypeVerification
            {
                ChangeKind = g.Key,
                PushedCount = g.Count(),
                VerifiedCount = 0 // differences mean these were NOT accepted
            }).ToImmutableArray();

            return new PushVerificationResult
            {
                IsFullyAccepted = false,
                EntityTypes = entityTypes
            };
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
