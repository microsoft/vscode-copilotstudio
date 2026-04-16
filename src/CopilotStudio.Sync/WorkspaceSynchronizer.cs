// Copyright (C) Microsoft Corporation. All rights reserved.
// Ported from om/src/vscode/LanguageServers/PowerPlatformLS/Impl.PullAgent/Sync/WorkspaceSynchronizer.cs
// Key changes: ILspLogger → ISyncProgress, DataverseClient → ISyncDataverseClient

using Microsoft.CopilotStudio.Sync.Dataverse;
using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.ObjectModel.FileProjection;
using Microsoft.Agents.ObjectModel.Merge;
using Microsoft.Agents.ObjectModel.Yaml;
using Microsoft.Agents.Platform.Content;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
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

    // Folder where environment variables are projected.
    private const string EnvironmentVariablesFolder = "environmentvariables";

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

    private static readonly string KnowledgeFilesSubPath = Path.Combine("knowledge", "files");

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
        Guid? agentId,
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
            agentId,
            null,
            cancellationToken).ConfigureAwait(false);

        if (result.Definition is BotComponentCollectionDefinition collection && collection.ComponentCollection is not null)
        {
            referenceTracker.MarkDeclaration(collection.GetRootSchemaName(), workspaceFolder);
        }

        // On clone, if there is no GptComponentMetadata (Agent.mcs.yml), write a default one.
        var isAgent = result.Definition is BotDefinition;
        if (isAgent && !HasGptComponentMetadata(result.Changeset))
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
                var yaml = await sr.ReadToEndAsync(cancellation).ConfigureAwait(false);
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
        Guid? agentId,
        CancellationToken cancellationToken,
        bool downloadAllKnowledgeFiles = false)
    {
        var fileAccessor = _fileAccessorFactory.Create(workspaceFolder);

        var workflows = await GetWorkflowsAsync(workspaceFolder, dataverseClient, agentId, fileAccessor, cancellationToken).ConfigureAwait(false);
        var mergedConnectionReferences = previousDefinition.ConnectionReferences.Concat(workflows.ConnectionReferences)
               .Where(cr => !string.IsNullOrEmpty(cr.ConnectionReferenceLogicalName.Value))
               .GroupBy(cr => cr.ConnectionReferenceLogicalName.Value, StringComparer.OrdinalIgnoreCase)
               .Select(g => g.Last())
               .ToList();
        previousDefinition = previousDefinition.WithFlows(workflows.Workflows).WithConnectionReferences(mergedConnectionReferences);

        // Collect change conflicts
        var localChanges = await GetLocalChangesAsync(workspaceFolder, previousDefinition, dataverseClient, agentId, cancellationToken).ConfigureAwait(false);
        var remoteChanges = await GetRemoteChangesAsync(workspaceFolder, operationContext, dataverseClient, agentId, cancellationToken).ConfigureAwait(false);
        var localChangesWithoutKnowledgeFiles = localChanges.Item2.Where(c => c.ChangeKind != BotElementKind.FileAttachmentComponent.ToString()).ToImmutableArray();

        var conflictingChanges = GetConflicts(localChangesWithoutKnowledgeFiles, remoteChanges.Item2);

        var remoteChangeset = remoteChanges.Item1;

        var originalSnapshot = ReadCloudCacheSnapshot(fileAccessor);

        // Apply raw changeSet on cloud cache
        var (newSnapshot, _) = UpdateCloudCache(fileAccessor, remoteChangeset, workflows);

        // Persist new delta token
        await WriteChangeTokenAsync(fileAccessor, remoteChangeset, cancellationToken).ConfigureAwait(false);

        var updatedChangeSetBuilder = remoteChangeset.ToBuilder();
        if (conflictingChanges.Length != 0)
        {
            //  Apply 3-way diff on conflicting items and update changeSet
            foreach (var schemaName in conflictingChanges)
            {
                var localChange = localChanges.Item1.BotComponentChanges.OfType<BotComponentUpsert>().FirstOrDefault(c => c.Component?.SchemaNameString == schemaName)?.Component;

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
        if (localChanges.Item1.Bot != null && remoteChanges.Item1.Bot != null && localChanges.Item1.Bot.Version != remoteChanges.Item1.Bot.Version)
        {
            var originalEntity = (originalSnapshot as BotDefinition)?.Entity;
            var originalComponentYaml = originalEntity == null ? null : GetMcsYaml(originalEntity.WithOnlySettingsYamlProperties());
            var localYaml = localChanges.Item1.Bot == null ? null : GetMcsYaml(localChanges.Item1.Bot);
            var remoteYaml = remoteChanges.Item1.Bot == null ? null : GetMcsYaml(remoteChanges.Item1.Bot.WithOnlySettingsYamlProperties());

            var updatedEntityString = MergeStrings(originalComponentYaml, localYaml, remoteYaml);

            // remoteChanges.Item1.Bot is non-null — guarded by the if-condition on line 234
            var remoteBot = remoteChanges.Item1.Bot!;
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
            updatedChangeSet = updatedChangeSetBuilder.Build().WithBot(remoteChanges.Item1.Bot);
        }

        // Filter out components that are identical to the pre-pull cloud cache snapshot.
        // This prevents silently overwriting local edits when the server returns a full
        // component set (non-delta) instead of just the changed components.
        updatedChangeSet = FilterUnchangedComponents(updatedChangeSet, originalSnapshot);

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
            await Parallel.ForEachAsync(newSnapshot.Components.OfType<FileAttachmentComponent>().Where(c => !string.IsNullOrEmpty(c.DisplayName)).ToList(), new ParallelOptions
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
        }

        // persist updated change set on directory
        return await UpdateWorkspaceDirectoryAsync(fileAccessor, updatedChangeSet, previousDefinition, deletedComponents.ToArray(), cancellationToken: cancellationToken).ConfigureAwait(false);
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

    private string MergeStrings(
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

    private BotComponentBase? MergeComponent(
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

    private ImmutableArray<string> GetConflicts(ImmutableArray<Change> local, ImmutableArray<Change> remote)
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

    public async Task<int> PushChangesetAsync(
        DirectoryPath workspaceFolder,
        AuthoringOperationContextBase operationContext, // includes login info
        PvaComponentChangeSet pushChangeset, // local changes to push up.
        ISyncDataverseClient dataverseClient,
        Guid? agentId,
        CloudFlowMetadata? cloudFlowMetadata,
        CancellationToken cancellationToken,
        bool uploadAllKnowledgeFiles = false)
    {
        // Upload will atomically:
        //  - send up changes,
        //  - receive new changes - including "confirmation" changes for the *files we just updated*
        // - This will include new version numbers (especially for newly created components) and new changetoken.
        var changeset = await _islandControlPlaneService.SaveChangesAsync(
            operationContext,
            pushChangeset,
            cancellationToken).ConfigureAwait(false);

        await WriteChangeSetAsync(workspaceFolder, changeset, cloudFlowMetadata, cancellationToken).ConfigureAwait(false);

        if (!uploadAllKnowledgeFiles)
        {
            return 0;
        }

        // Upload all knowledge files
        var fileAccessor = _fileAccessorFactory.Create(workspaceFolder);
        var snapshot = ReadCloudCacheSnapshot(fileAccessor);
        var numberOfUploadedFiles = 0;

        if (snapshot != null)
        {
            var newFileComponents = snapshot.Components
                .OfType<FileAttachmentComponent>()
                .Where(c => pushChangeset.BotComponentChanges.OfType<BotComponentUpsert>().Any(u => u.Component?.DisplayName == c.DisplayName))
                .ToList();

            if (newFileComponents.Count == 0)
            {
                return 0;
            }

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
                if (!IsValidFileToUpload(componentPath))
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
        }

        return numberOfUploadedFiles;
    }

    public virtual async Task ProvisionConnectionReferencesAsync(
        DefinitionBase definition,
        ISyncDataverseClient dataverseClient,
        CancellationToken cancellationToken)
    {
        var connectionRefs = definition.ConnectionReferences;

        foreach (var connRef in connectionRefs)
        {
            try
            {
                await dataverseClient.EnsureConnectionReferenceExistsAsync(
                    connRef.ConnectionReferenceLogicalName.ToString(),
                    connRef.ConnectorId.ToString(),
                    cancellationToken).ConfigureAwait(false);
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
    /// Sync workspace to write bot definition, git ignore, change token files in .mcs.
    /// </summary>
    /// <param name="workspaceFolder">Workspace folder.</param>
    /// <param name="operationContext">Context.</param>
    /// <param name="changeToken">Change token.</param>
    /// <param name="updateWorkspaceDirectory">Whether to update workspace directory.</param>
    /// <param name="dataverseClient">The dataverse client to use for communication with the dataverse service.</param>
    /// <param name="agentId">The ID of the agent.</param>
    /// <param name="cloudFlowMetadata">The cloud flow metadata containing workflow definitions and connection references.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    /// <returns>Workspace sync result.</returns>
    public async Task<WorkspaceSyncInfo> SyncWorkspaceAsync(
        DirectoryPath workspaceFolder,
        AuthoringOperationContextBase operationContext,
        string? changeToken,
        bool updateWorkspaceDirectory,
        ISyncDataverseClient dataverseClient,
        Guid? agentId,
        CloudFlowMetadata? cloudFlowMetadata,
        CancellationToken cancellationToken)
    {
        var fileAccessor = _fileAccessorFactory.Create(workspaceFolder);
        var workflows = cloudFlowMetadata ?? await GetWorkflowsAsync(workspaceFolder, dataverseClient, agentId, fileAccessor, cancellationToken).ConfigureAwait(false);
        var changeset = await _islandControlPlaneService.GetComponentsAsync(operationContext, changeToken, cancellationToken).ConfigureAwait(false);

        DefinitionBase emptyDefinition = operationContext switch
        {
            BotComponentCollectionAuthoringOperationContext => new BotComponentCollectionDefinition(),
            _ => new BotDefinition(flows: workflows.Workflows, connectionReferences: workflows.ConnectionReferences),
        };

        var definition = emptyDefinition.ApplyChanges(changeset);

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
        CancellationToken cancellationToken)
    {
        var fileAccessor = _fileAccessorFactory.Create(workspaceFolder);
        var (definition, deletedComponents) = UpdateCloudCache(fileAccessor, changeset, cloudFlowMetadata);
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
            SyncProgress = _syncProgress
        };

        // Write connectionreferences.mcs.yml with updated connection references from cloud.
        await WriteConnectionReferencesAsync(fileAccessor, updatedDefinition, cancellationToken).ConfigureAwait(false);

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

    private static string GetAgentSchemaName(string fullSchema) => fullSchema[..(fullSchema.IndexOf('.') is var i && i > 0 ? i : fullSchema.Length)];
    
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

        using var file = fileAccessor.OpenWrite(SettingsPath);
        using var sw = new StreamWriter(file, new UTF8Encoding(false));

        using var yamlContext = YamlSerializationContext.UseStandardSerializationContextIfNotDefined(throwOnInvalidYaml: false);
        YamlSerializer.SerializeWithoutKind(sw, entity.WithOnlySettingsYamlProperties());
    }

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
    private (DefinitionBase newCache, ImmutableArray<BotComponentBase> deletedComponents) UpdateCloudCache(IFileAccessor fileAccessor, PvaComponentChangeSet changeset, CloudFlowMetadata? cloudFlowMetadata = null)
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

        WriteCloudCache(fileAccessor, newSnapshot);
        return (newSnapshot, deletedComponents.ToImmutable());
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
    public async Task<(PvaComponentChangeSet, ImmutableArray<Change>)> GetLocalChangesAsync(DirectoryPath workspaceFolder, DefinitionBase workspaceDefinition, ISyncDataverseClient dataverseClient, Guid? agentId, CancellationToken cancellationToken)
    {
        var fileAccessor = _fileAccessorFactory.Create(workspaceFolder);
        var cloudSnapshot = ReadCloudCacheSnapshot(fileAccessor);
        if (cloudSnapshot == null)
        {
            throw new InvalidOperationException($"Unable to read cloud cache from .mcs/botdefinition.json");
        }

        var changeToken = await GetChangeTokenOrNullAsync(fileAccessor, cancellationToken).ConfigureAwait(false);
        var (changeSet, changes) = GetLocalChanges(workspaceDefinition, cloudSnapshot, fileAccessor, changeToken);

        var workflowChanges = GetLocalWorkflowChangesAsync(workspaceFolder, dataverseClient, agentId, fileAccessor, cloudSnapshot, cancellationToken);
        changes = changes.AddRange(await workflowChanges.ConfigureAwait(false));

        return (changeSet, changes);
    }

    // Determine remote changes by comparing the user files to the cloud cache. 
    public async Task<(PvaComponentChangeSet, ImmutableArray<Change>)> GetRemoteChangesAsync(DirectoryPath workspaceFolder, AuthoringOperationContextBase operationContext, ISyncDataverseClient dataverseClient, Guid? agentId, CancellationToken cancellationToken)
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
        var workflowChanges = GetRemoteWorkflowChangesAsync(dataverseClient, agentId, cloudSnapshot, cancellationToken);
        changes = changes.AddRange(await workflowChanges.ConfigureAwait(false));

        return (changeset, changes);
    }

    public (PvaComponentChangeSet, ImmutableArray<Change>) GetLocalChanges(DefinitionBase localDefinition, DefinitionBase cloudSnapshot, IFileAccessor fileAccessor, string? changeToken, bool isRemoteChange = false)
    {
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
                var parentSchemaName = localDefinition.VerifiedGetBotComponentById(localComponent.ParentBotComponentId).SchemaNameString;
                if (cloudSnapshot.TryGetComponentBySchemaName(parentSchemaName, out var cloudComponentParent))
                {
                    parentBotComponentId = cloudComponentParent.Id;
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

        var changeset = new PvaComponentChangeSet(
              botComponentBuilderList,
              null,
              environmentVariableChanges,
              null,
              null,
              null,
              null,
              null,
              null,
              botEntity,
              changeToken);

        return (changeset, changes.ToImmutable());
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

            var path = new AgentFilePath(PathResolver.GetComponentPath(groundedComponent, Definition));
            using var stream = FileAccessor.OpenWrite(path);
            using var textWriter = new StreamWriter(stream);
            CodeSerializer.SerializeAsMcsYml(textWriter, groundedComponent);
        }
    }

    public virtual async Task<(ImmutableArray<WorkflowResponse>, CloudFlowMetadata)> UpsertWorkflowForAgentAsync(DirectoryPath workspaceFolder, ISyncDataverseClient dataverseClient, Guid? agentId, CancellationToken cancellationToken)
    {
        if (agentId == null || agentId == Guid.Empty)
        {
            return (ImmutableArray<WorkflowResponse>.Empty, new CloudFlowMetadata
            {
                Workflows = ImmutableArray<CloudFlowDefinition>.Empty,
                ConnectionReferences = ImmutableArray<ConnectionReference>.Empty
            });
        }

        var cloudFlowDefinitions = new List<CloudFlowDefinition>();
        var workflows = new List<WorkflowMetadata>();
        var connectionReferences = ImmutableArray<ConnectionReference>.Empty;
        var workflowResponseBuilder = ImmutableArray.CreateBuilder<WorkflowResponse>();

        if (agentId.HasValue && agentId != Guid.Empty)
        {
            var workflowsDir = Path.Combine(workspaceFolder.ToString(), WorkflowFolder);
            if (Directory.Exists(workflowsDir))
            {
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

                    var clientDataJson = await File.ReadAllTextAsync(jsonFile, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
                    var yamlText = await File.ReadAllTextAsync(metadataFile, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
                    var deserializer = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).Build();
                    var metadata = deserializer.Deserialize<WorkflowMetadata>(yamlText);
                    metadata.ClientData = clientDataJson;
                    workflows.Add(metadata);

                    var workflowResponse = await dataverseClient.UpdateWorkflowAsync(agentId.Value, metadata, cancellationToken).ConfigureAwait(false);

                    var (cloudFlowDefinition, _) = GetFlowDefinition(metadata);
                    cloudFlowDefinitions.Add(cloudFlowDefinition);
                    workflowResponseBuilder.Add(workflowResponse);
                }

                connectionReferences = await GetConnectionReferenceFromLogicalNamesAsync(GetConnectionReferenceLogicalNamesFromFlows(workflows), dataverseClient, cancellationToken).ConfigureAwait(false);
            }
        }

        return (workflowResponseBuilder.ToImmutable(), new CloudFlowMetadata
        {
            Workflows = cloudFlowDefinitions.ToImmutableArray(),
            ConnectionReferences = connectionReferences
        });
    }

    public async Task<CloudFlowMetadata> GetWorkflowsAsync(DirectoryPath workspaceFolder, ISyncDataverseClient dataverseClient, Guid? agentId, IFileAccessor fileAccessor, CancellationToken cancellationToken)
    {
        var cloudFlowDefinitions = new List<CloudFlowDefinition>();
        var workflows = new List<WorkflowMetadata>();
        var connectionReferences = ImmutableArray<ConnectionReference>.Empty;

        try
        {
            var remote = await dataverseClient.DownloadAllWorkflowsForAgentAsync(agentId, cancellationToken).ConfigureAwait(false);
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

                    using var jsonDoc = JsonDocument.Parse(workflow.ClientData);
                    var jsonString = JsonSerializer.Serialize(jsonDoc.RootElement, new JsonSerializerOptions { WriteIndented = true });

                    var jsonStream = fileAccessor.OpenWrite(workflowJsonTmp);
                    await using (jsonStream.ConfigureAwait(false))
                    {
                        var writer = new StreamWriter(jsonStream, Encoding.UTF8);
                        await using (writer.ConfigureAwait(false))
                        {
                            await writer.WriteAsync(jsonString).ConfigureAwait(false);
                        }
                    }
                    fileAccessor.Replace(workflowJsonTmp, workflowJson);

                    var workflowMetadata = new AgentFilePath($"{workflowFolder}/metadata.yml");
                    var workflowMetadataTmp = new AgentFilePath($"{workflowFolder}/metadata.yml.tmp");
                    var serializer = new SerializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).Build();

                    var metaStream = fileAccessor.OpenWrite(workflowMetadataTmp);
                    await using (metaStream.ConfigureAwait(false))
                    {
                        var writer = new StreamWriter(metaStream, Encoding.UTF8);
                        await using (writer.ConfigureAwait(false))
                        {
                            serializer.Serialize(writer, workflow);
                        }
                    }
                    fileAccessor.Replace(workflowMetadataTmp, workflowMetadata);
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
        catch (Exception ex)
        {
            _syncProgress.Report($"Failed to download workflows for agent {agentId}. Exception: {ex.Message}");
        }

        return new CloudFlowMetadata
        {
            Workflows = cloudFlowDefinitions.ToImmutableArray(),
            ConnectionReferences = connectionReferences
        };
    }

    public async Task<DefinitionBase> ReadWorkspaceDefinitionAsync(DirectoryPath workspaceFolder, CancellationToken cancellationToken, bool checkKnowledgeFiles = false)
    {
        var fileAccessor = _fileAccessorFactory.Create(workspaceFolder);
        var definition = ReadCloudCacheSnapshot(fileAccessor);
        if (definition == null)
        {
            throw new FileNotFoundException(".mcs/botdefinition.json was not found. Please resync.");
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

            var relativePath = _pathResolver.GetComponentPath(component, definition);
            var filePath = new AgentFilePath(relativePath);

            if (!fileAccessor.Exists(filePath))
            {
                // User deleted the file — omit from definition
                continue;
            }

            using var stream = fileAccessor.OpenRead(filePath);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var yaml = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

            var deserialized = CodeSerializer.Deserialize(yaml, component.RootElement?.GetType() ?? typeof(BotElement), null);
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

        if (checkKnowledgeFiles)
        {
            // Detect new knowledge files
            var knowledgeFiles = fileAccessor.ListFiles(KnowledgeFilesSubPath, "*.*").Where(f => !f.FileName.EndsWith(".mcs.yml", StringComparison.OrdinalIgnoreCase)).ToList();
            var existingKnowledgeNames = definition.Components.OfType<FileAttachmentComponent>().Select(c => c.DisplayName).Where(n => !string.IsNullOrEmpty(n)).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var file in knowledgeFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (existingKnowledgeNames.Contains(file.FileName))
                {
                    continue;
                }

                if (IsValidFileToUpload(file))
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

        return definition.WithComponents(updatedComponents).WithEnvironmentVariables(updatedEnvVars);
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
            var yaml = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

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

    private bool IsValidFileToUpload(AgentFilePath knowledgeFile)
    {
        var maxFileSize = 125L * 1024 * 1024; // 125 MB
        var fileInfo = new FileInfo(knowledgeFile.ToString());

        if (fileInfo.Exists && fileInfo.Length > maxFileSize)
        {
            _syncProgress.Report($"File '{knowledgeFile.FileName}' exceeded file size limit of 125MB and will be skipped.");
        }

        return fileInfo.Exists && fileInfo.Length <= maxFileSize;
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

    private async Task<ImmutableArray<Change>> GetLocalWorkflowChangesAsync(DirectoryPath workspaceFolder, ISyncDataverseClient dataverseClient, Guid? agentId, IFileAccessor fileAccessor, DefinitionBase originalDefinition, CancellationToken cancellationToken)
    {
        var localContent = await GetLocalWorkflowContentAsync(workspaceFolder, dataverseClient, agentId, fileAccessor, cancellationToken).ConfigureAwait(false);
        var originalContent = await GetOriginalWorkflowContentAsync(originalDefinition, dataverseClient, cancellationToken).ConfigureAwait(false);
        return ComputeWorkflowChanges(originalContent, localContent, isLocal: true);
    }

    private async Task<ImmutableArray<Change>> GetRemoteWorkflowChangesAsync(ISyncDataverseClient dataverseClient, Guid? agentId, DefinitionBase originalDefinition, CancellationToken cancellationToken)
    {
        var remoteContent = await GetRemoteWorkflowContentAsync(dataverseClient, agentId, cancellationToken).ConfigureAwait(false);
        var originalContent = await GetOriginalWorkflowContentAsync(originalDefinition, dataverseClient, cancellationToken).ConfigureAwait(false);
        return ComputeWorkflowChanges(originalContent, remoteContent, isLocal: false);
    }

    private async Task<CloudFlowMetadata> GetLocalWorkflowContentAsync(DirectoryPath workspaceFolder, ISyncDataverseClient dataverseClient, Guid? agentId, IFileAccessor fileAccessor, CancellationToken cancellationToken)
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

            var yaml = await File.ReadAllTextAsync(metadataFile, cancellationToken).ConfigureAwait(false);
            var json = await File.ReadAllTextAsync(jsonFile, cancellationToken).ConfigureAwait(false);
            var deserializer = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).Build();
            var metadata = deserializer.Deserialize<WorkflowMetadata>(yaml);
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

    private async Task<CloudFlowMetadata> GetRemoteWorkflowContentAsync(ISyncDataverseClient dataverseClient, Guid? agentId, CancellationToken cancellationToken)
    {
        var cloudFlowDefinitions = new List<CloudFlowDefinition>();
        var workflows = new List<WorkflowMetadata>();
        var remote = await dataverseClient.DownloadAllWorkflowsForAgentAsync(agentId, cancellationToken).ConfigureAwait(false);

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
            var (workflowJsonPath, _) = GetWorkflowPath(workflow.DisplayName, workflowId);
            var remoteContent = !isLocal ? GetClientData(workflow) : null;

            if (!originalMap.ContainsKey(workflowId))
            {
                changes.Add(new Change
                {
                    Name = workflow.DisplayName ?? workflowId.ToString(),
                    Uri = workflowJsonPath.ToString(),
                    ChangeType = ChangeType.Create,
                    ChangeKind = BotElementKind.CloudFlowDefinition.ToString(),
                    SchemaName = $"Mcs.Workflow.{workflowId}",
                    RemoteWorkflowContent = remoteContent
                });
            }
            else
            {
                var originalWorkflow = originalMap[workflowId];

                if (GetClientData(workflow) != GetClientData(originalWorkflow))
                {
                    changes.Add(new Change
                    {
                        Name = workflow.DisplayName ?? workflowId.ToString(),
                        Uri = workflowJsonPath.ToString(),
                        ChangeType = ChangeType.Update,
                        ChangeKind = BotElementKind.CloudFlowDefinition.ToString(),
                        SchemaName = $"Mcs.Workflow.{workflowId}",
                        RemoteWorkflowContent = remoteContent
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
            references = result.Select(dto => new ConnectionReference(connectionReferenceLogicalName: dto.ConnectionReferenceLogicalName, connectionId: dto.ConnectionReferenceId.ToString(), connectorId: dto.ConnectorId ?? throw new InvalidOperationException($"ConnectorId missing for connection reference {dto.ConnectionReferenceLogicalName}"))).ToImmutableArray();
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

            using var doc = JsonDocument.Parse(workflow.ClientData);
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
                using var doc = JsonDocument.Parse(s.Value);
                return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
            }
            catch (JsonException)
            {
                return s.Value;
            }
        }

        return string.Empty;
    }

    private static (CloudFlowDefinition, ImmutableArray<string>) GetFlowDefinition(WorkflowMetadata workflow)
    {
        RecordDataType? inputType = null;
        RecordDataType? outputType = null;
        var workflowConnectionNames = ImmutableArray<string>.Empty;
        if (!string.IsNullOrWhiteSpace(workflow.ClientData))
        {
            using var document = JsonDocument.Parse(workflow.ClientData);
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
            extensionData: !string.IsNullOrWhiteSpace(workflow.ClientData) ? new RecordDataValue(ImmutableDictionary<string, DataValue>.Empty.Add("clientdata", DataValue.Create(workflow.ClientData))) : null
        );

        return (cloudFlowDefinition, workflowConnectionNames);
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

        var dict = new Dictionary<string, PropertyInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var actionProperty in actions.EnumerateObject())
        {
            if (!actionProperty.Value.TryGetProperty("type", out var typeNode) || !string.Equals(typeNode.GetString(), "Response", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (actionProperty.Value.TryGetProperty("inputs", out var inputs) && inputs.TryGetProperty("schema", out var schema) && schema.TryGetProperty("properties", out var outputProps))
            {
                foreach (var prop in outputProps.EnumerateObject())
                {
                    if (!dict.ContainsKey(prop.Name))
                    {
                        dict[prop.Name] = CreatePropertyInfoFromJson(prop.Value, prop.Name);
                    }
                }
            }
        }

        return dict.Count == 0 ? null : new RecordDataType(dict.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase));
    }

    private static PropertyInfo CreatePropertyInfoFromJson(JsonElement propValue, string propName)
    {
        DataType type = DataType.String;
        PropertyInfo? property;

        if (propValue.TryGetProperty("type", out var typeNode))
        {
            type = MapFlowType(typeNode.GetString() ?? "string");
        }

        if (propValue.TryGetProperty("x-ms-content-hint", out var hintNode) && string.Equals(hintNode.GetString(), "FILE", StringComparison.OrdinalIgnoreCase))
        {
            property = PropertyInfo.EmptyRecord;
        }
        else
        {
            property = new PropertyInfo(
                displayName: propValue.TryGetProperty("title", out var titleNode) ? titleNode.GetString() : propName,
                description: propValue.TryGetProperty("description", out var descNode) ? descNode.GetString() : null,
                isRequired: false,
                type: type
            );
        }

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

    private Task WriteConnectionReferencesAsync(IFileAccessor fileAccessor, DefinitionBase definition, CancellationToken cancellationToken)
    {
        var uniqueConnectionReferences = definition.ConnectionReferences
                .Where(cr => !string.IsNullOrEmpty(cr.ConnectionReferenceLogicalName.Value))
                .GroupBy(cr => cr.ConnectionReferenceLogicalName.Value, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.Last())
                .ToList();

        // Write connectionreferences.mcs.yml if there are any connection references
        if (uniqueConnectionReferences.Any())
        {
            using var file = fileAccessor.OpenWrite(ConnectionReferencesPath);
            using var sw = new StreamWriter(file, Encoding.UTF8);
            using var yamlContext = YamlSerializationContext.UseStandardSerializationContextIfNotDefined(throwOnInvalidYaml: false);

            CodeSerializer.SerializeConnectionReferences(sw, uniqueConnectionReferences);
        }

        return Task.CompletedTask;
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
            await CloneChangesAsync(folder, referenceTracker, operationContext, dataverseClient, syncInfo.AgentId, cancellationToken).ConfigureAwait(false);
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
        Guid? agentId,
        CancellationToken cancellationToken)
    {
        // Read the pushed (expected) workspace definition
        var expectedDefinition = await ReadWorkspaceDefinitionAsync(workspaceFolder, cancellationToken).ConfigureAwait(false);

        // Clone to a temp workspace to get the server's current state
        var tempDir = Path.Combine(Path.GetTempPath(), "mcs-verify-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        var tempWorkspace = new DirectoryPath(tempDir.Replace('\\', '/'));

        try
        {
            var referenceTracker = new ReferenceTracker();
            await CloneChangesAsync(tempWorkspace, referenceTracker, operationContext, dataverseClient, agentId, cancellationToken).ConfigureAwait(false);

            var serverDefinition = await ReadWorkspaceDefinitionAsync(tempWorkspace, cancellationToken).ConfigureAwait(false);

            // Compare per-entity-type: group expected changes by ChangeKind, count matches in server state
            var (_, expectedChanges) = await GetLocalChangesAsync(tempWorkspace, expectedDefinition, dataverseClient, agentId, cancellationToken).ConfigureAwait(false);

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
