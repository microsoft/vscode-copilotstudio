namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.Agents.ObjectModel.Merge;
    using Microsoft.Agents.ObjectModel.Yaml;
    using Microsoft.Agents.Platform.Content;
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.PowerPlatformLS.Contracts.FileLayout;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using Microsoft.PowerPlatformLS.Impl.PullAgent.Dataverse;
    using System.Collections.Immutable;
    using System.IO;
    using System.Text;
    using System.Text.Json;
    using YamlDotNet.Serialization;
    using YamlDotNet.Serialization.NamingConventions;
    using static Microsoft.PowerPlatformLS.Impl.PullAgent.Dataverse.DataverseClient;

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

        /// <summary>
        /// Icon path within the agent.
        /// </summary>
        private static readonly AgentFilePath IconPath = new AgentFilePath("icon.png");

        private readonly IMcsFileParser _fileParser;
        private readonly IComponentPathResolver _pathResolver;
        private readonly IFileAccessorFactory _fileAccessorFactory;
        private readonly IIslandControlPlaneService _islandControlPlaneService;

        public WorkspaceSynchronizer(
            IMcsFileParser fileParser,
            IFileAccessorFactory writer,
            IIslandControlPlaneService islandControlPlanService,
            ILspLogger logger,
            IComponentPathResolver pathResolver)
        {
            _fileParser = fileParser;
            _fileAccessorFactory = writer;
            _islandControlPlaneService = islandControlPlanService;
            _logger = logger;
            _pathResolver = pathResolver;
        }

        private readonly ILspLogger _logger;

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

            if (validConnections.Count < definition.ConnectionReferences.Count())
            {
                var removed = definition.ConnectionReferences.Count() - validConnections.Count;
                _logger.LogInformation($"Removed {removed} orphaned connection reference(s) with no workspace usage");
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
            // Write connectionreferences.mcs.yml if there are any connection references
            if (definition.ConnectionReferences.Any())
            {
                using var file = fileAccessor.OpenWrite(ConnectionReferencesPath);
                using var sw = new StreamWriter(file, Encoding.UTF8);
                using var yamlContext = YamlSerializationContext.UseStandardSerializationContextIfNotDefined(throwOnInvalidYaml: false);
                
                CodeSerializer.SerializeConnectionReferences(sw, definition.ConnectionReferences);
            }

            return Task.CompletedTask;
        }

        public async Task CloneChangesAsync(
            DirectoryPath workspaceFolder,
            ReferenceTracker referenceTracker,
            AuthoringOperationContextBase operationContext,
            DataverseClient dataverseClient,
            Guid? agentId,
            CancellationToken cancellationToken)
        {
            var fileAccessor = _fileAccessorFactory.Create(workspaceFolder);
            string? changeToken = await GetChangeTokenOrNullAsync(fileAccessor, cancellationToken);

            var result = await SyncWorkspaceAsync(
                workspaceFolder,
                operationContext,
                changeToken,
                updateWorkspaceDirectory: true,
                dataverseClient,
                agentId,
                null,
                cancellationToken);

            if (result.Definition is BotComponentCollectionDefinition collection && collection.ComponentCollection is not null)
            {
                referenceTracker.MarkDeclaration(collection.GetRootSchemaName(), workspaceFolder);
            }

            // On clone, if there is no GptComponentMetadata (Agent.mcs.yml), write a default one.
            bool isAgent = result.Definition is BotDefinition;
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
                    var yaml = await sr.ReadToEndAsync(cancellation);
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
            bool hasGptComponentMetadata = changeSet.BotComponentChanges.Any(change =>
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
            DataverseClient dataverseClient,
            Guid? agentId,
            CancellationToken cancellationToken)
        {
            var fileAccessor = _fileAccessorFactory.Create(workspaceFolder);

            var workflows = await GetWorkflowsAsync(workspaceFolder, dataverseClient, agentId, fileAccessor, cancellationToken);
            previousDefinition = previousDefinition.WithFlows(workflows.Workflows).WithConnectionReferences(workflows.ConnectionReferences);

            // Collect change conflicts
            var localChanges = await GetLocalChangesAsync(workspaceFolder, previousDefinition, dataverseClient, agentId, cancellationToken);
            var remoteChanges = await GetRemoteChangesAsync(workspaceFolder, operationContext, dataverseClient, agentId, cancellationToken);
            var conflictingChanges = GetConflicts(localChanges.Item2, remoteChanges.Item2);

            var remoteChangeset = remoteChanges.Item1;

            var originalSnapshot = ReadCloudCacheSnapshot(fileAccessor);

            // Apply raw changeSet on cloud cache
            UpdateCloudCache(fileAccessor, remoteChangeset, workflows);
            // Persist new delta token
            await WriteChangeTokenAsync(fileAccessor, remoteChangeset, cancellationToken);

            var updatedChangeSetBuilder = remoteChangeset.ToBuilder();
            if (conflictingChanges.Length != 0)
            {
                //  Apply 3-way diff on conflicting items and update changeSet
                foreach (var schemaName in conflictingChanges)
                {
                    BotComponentBase? localChange = localChanges.Item1.BotComponentChanges.OfType<BotComponentUpsert>().FirstOrDefault(c => c.Component?.SchemaNameString == schemaName)?.Component;

                    var remoteChange = remoteChangeset.BotComponentChanges.OfType<BotComponentUpsert>().FirstOrDefault(c => c.Component?.SchemaNameString == schemaName);
                    BotComponentBase ? remoteChangeComponent = remoteChange?.Component;
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
                string? originalComponentYaml = originalEntity == null ? null : GetMcsYaml(originalEntity.WithOnlySettingsYamlProperties());
                string? localYaml = localChanges.Item1.Bot == null? null : GetMcsYaml(localChanges.Item1.Bot);
                string? remoteYaml = remoteChanges.Item1.Bot == null ? null : GetMcsYaml(remoteChanges.Item1.Bot.WithOnlySettingsYamlProperties());

                var updatedEntityString = MergeStrings(originalComponentYaml, localYaml, remoteYaml);

                // ! null allowed
                var bot = (CodeSerializer.Deserialize<BotEntity>(updatedEntityString) ?? remoteChanges.Item1.Bot ?? originalEntity)!;
                bot = bot.WithVersion(remoteChanges.Item1.Bot?.Version ?? originalEntity?.Version ?? 0);
                updatedChangeSetBuilder.Bot = bot;
                updatedChangeSet = updatedChangeSetBuilder.Build();
            }
            else
            {
                updatedChangeSet = updatedChangeSetBuilder.Build().WithBot(remoteChanges.Item1.Bot);
            }

            var deletedComponents = ImmutableArray.CreateBuilder<BotComponentBase>();
            foreach (var item in updatedChangeSet.BotComponentChanges.OfType<BotComponentDelete>())
            {
                if (originalSnapshot?.TryGetBotComponentById(item.BotComponentId, out var component) == true)
                {
                    deletedComponents.Add(component);
                }
            }

            // persist updated change set on directory
            return await UpdateWorkspaceDirectoryAsync(fileAccessor, updatedChangeSet, previousDefinition, deletedComponents.ToArray(), cancellationToken: cancellationToken);
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
                original == null ? null : new StringReader(original),
                encoding,
                diffOption);

            var localFile = DiffFile.Create(
                local == null ? null : new StringReader(local),
                encoding,
                diffOption);

            var remoteDiff = DiffFile.Create(
                remote == null ? null : new StringReader(remote),
                encoding,
                diffOption);


            var mergeOptions = new MergeOptions();
            mergeOptions.Flags |= DiffOptionFlags.IgnoreWhiteSpace;
            DiffLineComparer comparer = new DiffLineComparer(mergeOptions);

            var mergeList = MergeFinder.Merge(originalFile, localFile, remoteDiff, comparer, mergeOptions);

            // TODO: we can act on these summaries - as they describe how many conflicts were encountered, etc.
            var mergeSummary = MergeFinder.GetSummary(mergeList);

            var writer = new StringWriter();
            var outputEncoding = Encoding.UTF8;

            MergeOutput mergeOutput = new MergeOutput(mergeOptions, writer);
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

            string? localChangeYaml = localChange?.RootElement == null ? null : CodeSerializer.Serialize(localChange.RootElement);
            string? originalComponentYaml = GetMcsYaml(originalComponent);
            string? remoteChangeYaml = GetMcsYaml(remoteChange);

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

        public async Task PushChangesetAsync(
            DirectoryPath workspaceFolder,
            AuthoringOperationContextBase operationContext, // includes login info
            PvaComponentChangeSet pushChangeset, // local changes to push up.
            DataverseClient dataverseClient,
            Guid? agentId,
            CloudFlowMetadata? cloudFlowMetadata,
            CancellationToken cancellationToken)
        {
            // Upload will atomically:
            //  - send up changes,
            //  - receive new changes - including "confirmation" changes for the *files we just updated*
            // - This will include new version numbers (especially for newly created components) and new changetoken.
            var changeset = await _islandControlPlaneService.SaveChangesAsync(
                operationContext,
                pushChangeset,
                cancellationToken);

            await WriteChangeSetAsync(workspaceFolder, changeset, cloudFlowMetadata, cancellationToken);
        }

        public virtual async Task ProvisionConnectionReferencesAsync(
            DefinitionBase definition,
            DataverseClient dataverseClient,
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
                        cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Failed to provision connection '{connRef.ConnectionReferenceLogicalName}': {ex.Message}");
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
            DataverseClient dataverseClient,
            Guid? agentId,
            CloudFlowMetadata? cloudFlowMetadata,
            CancellationToken cancellationToken)
        {
            var fileAccessor = _fileAccessorFactory.Create(workspaceFolder);
            var workflows = cloudFlowMetadata ?? await GetWorkflowsAsync(workspaceFolder, dataverseClient, agentId, fileAccessor, cancellationToken);
            var changeset = await _islandControlPlaneService.GetComponentsAsync(operationContext, changeToken, cancellationToken);

            DefinitionBase emptyDefinition = operationContext switch
            {
                BotComponentCollectionAuthoringOperationContext => new BotComponentCollectionDefinition(),
                _ => new BotDefinition(flows: workflows.Workflows, connectionReferences: workflows.ConnectionReferences),
            };

            var definition = emptyDefinition.ApplyChanges(changeset);

            await fileAccessor.WriteAsync(GitIgnorePath, "*", cancellationToken);
            WriteCloudCache(fileAccessor, definition);

            await WriteChangeTokenAsync(fileAccessor, changeset, cancellationToken);

            if (updateWorkspaceDirectory)
            {
                await UpdateWorkspaceDirectoryAsync(
                    fileAccessor,
                    changeset,
                    definition,
                    deletedComponents: [],
                    cancellationToken: cancellationToken);
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
            await UpdateWorkspaceDirectoryAsync(fileAccessor, changeset, definition, deletedComponents, cancellationToken);
            await WriteChangeTokenAsync(fileAccessor, changeset, cancellationToken);
        }

        // Write the References.mcs.yml - only write a file if there are changes. 
        private Task WriteReferencesAsync(
            IFileAccessor fileAccessor,
            IEnumerable<BotComponentCollectionChange> changes,
            string thisSchema)
        {
            List<ReferenceItemSourceFile> items = new List<ReferenceItemSourceFile>();
            foreach (var change in changes)
            {
                if (change is BotComponentCollectionUpsert upsert)
                {
                    var cc = upsert.ComponentCollection;
                    if (cc != null && cc.SchemaName != thisSchema)
                    {
                        // Filepath will be filled in in 2nd pass after all components are synced. 
                        string filePath = "";
                        items.Add(new ReferenceItemSourceFile(cc.SchemaName, filePath));
                    }
                }
            }

            if (items.Count > 0)
            {
                ReferencesSourceFile refs = new ReferencesSourceFile(items);

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
            string thisSchema = string.Empty;
            if (definition is BotComponentCollectionDefinition collection)
            {
                var cc = changeset.ComponentCollectionChanges.OfType<BotComponentCollectionUpsert>().Select(cc => cc.ComponentCollection).FirstOrDefault(static d => d != null);
                WriteComponentCollection(fileAccessor, cc, cancellationToken);

                thisSchema = collection.GetRootSchemaName();
            }
            else if (definition is BotDefinition bot)
            {
                // Prefer changeset.Bot (fresh from cloud) if available, otherwise use bot.Entity from definition.
                await WriteBotEntityAsync(fileAccessor, changeset.Bot ?? bot.Entity, cancellationToken);
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
                PathResolver = _pathResolver
            };

            // Write connectionreferences.mcs.yml with updated connection references from cloud.
            await WriteConnectionReferencesAsync(fileAccessor, updatedDefinition, cancellationToken);

            // Write References.mcs.yml.
            await WriteReferencesAsync(fileAccessor, changeset.ComponentCollectionChanges, thisSchema);

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
            bool isReusable = component.ShareContext?.ReusePolicy is BotComponentReusePolicyWrapper reusePolicy
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

        private void WriteComponentCollection(IFileAccessor fileAccessor, BotComponentCollection? collection, CancellationToken cancellationToken)
        {
            if (collection == null)
            {
                return;
            }

            using var file = fileAccessor.OpenWrite(ComponentCollectionPath);
            using var sw = new StreamWriter(file, Encoding.UTF8);
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
                await fileAccessor.WriteAsync(IconPath, icon, cancellationToken);
            }

            using var file = fileAccessor.OpenWrite(SettingsPath);
            using var sw = new StreamWriter(file, Encoding.UTF8);

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
                JsonSerializer.Serialize<DefinitionBase>(stream, definition, ElementSerializer.CreateOptions());
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
                newSnapshot = newSnapshot.WithFlows(cloudFlowMetadata.Workflows).WithConnectionReferences(cloudFlowMetadata.ConnectionReferences);
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

            await fileAccessor.WriteAsync(ChangeTokenPath, changeToken, cancellationToken);
        }

        private async Task<string?> GetChangeTokenOrNullAsync(IFileAccessor fileAccessor, CancellationToken cancellationToken)
        {
            if (!fileAccessor.Exists(ChangeTokenPath))
            {
                return null;
            }

            return await fileAccessor.ReadStringAsync(ChangeTokenPath, cancellationToken);
        }

        public async Task SaveSyncInfoAsync(DirectoryPath workspaceFolder, AgentSyncInfo syncInfo)
        {
            var writer = _fileAccessorFactory.Create(workspaceFolder);
            writer.CreateHiddenDirectory(new(HiddenRoot));
            using var stream = writer.OpenWrite(ConnectionDetailsPath);
            await JsonSerializer.SerializeAsync(stream, syncInfo);
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
            var obj = await JsonSerializer.DeserializeAsync<AgentSyncInfo>(stream);
            // This only happens if the file contains the string "null".
            if (obj == null)
            {
                throw new InvalidOperationException($"Unable to process content in the connection file .mcs/conn.json.");
            }

            return obj;
        }

        // Determine local changes by comparing the user files to the cloud cache. 
        public async Task<(PvaComponentChangeSet, ImmutableArray<Change>)> GetLocalChangesAsync(DirectoryPath workspaceFolder, DefinitionBase workspaceDefinition, DataverseClient dataverseClient, Guid? agentId, CancellationToken cancellationToken)
        {
            var fileAccessor = _fileAccessorFactory.Create(workspaceFolder);
            var cloudSnapshot = ReadCloudCacheSnapshot(fileAccessor);
            if (cloudSnapshot == null)
            {
                throw new InvalidOperationException($"Unable to read cloud cache from .mcs/botdefinition.json");
            }

            var changeToken = await GetChangeTokenOrNullAsync(fileAccessor, cancellationToken);
            var (changeSet, changes) = GetLocalChanges(workspaceDefinition, cloudSnapshot, changeToken);

            var workflowChanges = GetLocalWorkflowChangesAsync(workspaceFolder, dataverseClient, agentId, fileAccessor, cloudSnapshot, cancellationToken);
            changes = changes.AddRange(await workflowChanges);

            return (changeSet, changes);
        }

        // Determine remote changes by comparing the user files to the cloud cache. 
        public async Task<(PvaComponentChangeSet, ImmutableArray<Change>)> GetRemoteChangesAsync(DirectoryPath workspaceFolder, AuthoringOperationContextBase operationContext, DataverseClient dataverseClient, Guid? agentId, CancellationToken cancellationToken)
        {
            var fileAccessor = _fileAccessorFactory.Create(workspaceFolder);
            string? changeToken = await GetChangeTokenOrNullAsync(fileAccessor, cancellationToken);
            var changeset = await _islandControlPlaneService.GetComponentsAsync(
                    operationContext,
                    changeToken: changeToken,
                    cancellationToken);

            var cloudSnapshot = ReadCloudCacheSnapshot(fileAccessor) ?? BotDefinition.Empty;

            var appliedDefinition = cloudSnapshot.ApplyChanges(changeset);
            var computeChanges = GetLocalChanges(appliedDefinition, cloudSnapshot, changeToken);

            var changes = computeChanges.Item2;
            var workflowChanges = GetRemoteWorkflowChangesAsync(dataverseClient, agentId, cloudSnapshot, cancellationToken);
            changes = changes.AddRange(await workflowChanges);

            return (changeset, changes);
        }

        public (PvaComponentChangeSet, ImmutableArray<Change>) GetLocalChanges(DefinitionBase localDefinition, DefinitionBase cloudSnapshot, string? changeToken)
        {
            var changes = ImmutableArray.CreateBuilder<Change>();
            var botComponentBuilderList = new List<BotComponentChange>();

            BotEntity? botEntity = null;
            BotEntityId parentBotId = default;
            BotComponentCollectionId parentCollectionId = default;
            if (localDefinition is BotComponentCollectionDefinition collection)
            {
                parentCollectionId = collection.ComponentCollection?.Id ?? throw new InvalidOperationException("Missing component collection definition");
                var cloudSnapshotCollection = (cloudSnapshot as BotDefinition)?.Entity;
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
                        var uri = botEntity.Syntax?.SourceUri.ToString() ?? settingsPathValue;
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
                    var uri = botEntity.Syntax?.SourceUri.ToString() ?? iconPathValue;
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
                if (localComponent is FileAttachmentComponent)
                {
                    // knowledge file local/remote change is handled in client side so skip in server.
                    continue;
                }

                if (IsReusableOrNonCustomizableComponent(localComponent))
                {
                    continue;
                }

                BotComponentId parentBotComponentId = default;
                // Remap local botIds (which were fabricated) to real botIds from the cloud. 
                if (localComponent.ParentBotComponentId.HasValue)
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
                    bool same =
                        (r1 is not null) &&
                        (r2 is not null) &&
                        r1.Equals(r2, NodeComparison.Structural) &&
                        (string.IsNullOrWhiteSpace(localComponent.DisplayName) || NormalizeString(localComponent.DisplayName) == NormalizeString(cloudComponent.DisplayName)) &&
                        (string.IsNullOrWhiteSpace(localComponent.Description) || NormalizeString(localComponent.Description) == NormalizeString(cloudComponent.Description));

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
                        // knowledge file local/remote change is handled in client side so skip in server.
                        continue;
                    }

                    botComponentBuilderList.Add(new BotComponentDelete(cloudComponent.Id, cloudComponent.Version));
                    changes.Add(new Change() { ChangeType = ChangeType.Delete, Name = cloudComponent.SchemaNameString, Uri = _pathResolver.GetComponentPath(cloudComponent, cloudSnapshot), SchemaName = cloudComponent.SchemaNameString, ChangeKind = cloudComponent.Kind.ToString() });
                }
            }

            var changeset = new PvaComponentChangeSet(
                  botComponentBuilderList,
                  botEntity,
                  changeToken);

            return (changeset, changes.ToImmutable());
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

            var sw = new StringWriter();
            if (element is BotComponentBase component)
            {
                CodeSerializer.Serialize(sw, component.RootElement);
            }
            else
            {
                CodeSerializer.Serialize(sw, element);
            }

            return sw.ToString();
        }

        class ComponentWriter : BotComponentChangeVisitor
        {
            public required IFileAccessor FileAccessor { get; init; }

            public required DefinitionBase Definition { get; init; }

            public required IComponentPathResolver PathResolver { get; init; }

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

                // Bot defintions from the PvaChangeSet are orphaned and not in a BotDefinition.
                // Need a the BotDef to do things like compute file path. 
                var groundedComponent = Definition.VerifiedGetBotComponentById(botComponent.Id);
                var path = new AgentFilePath(PathResolver.GetComponentPath(groundedComponent, Definition));
                using Stream stream = FileAccessor.OpenWrite(path);
                using var textWriter = new StreamWriter(stream);
                CodeSerializer.SerializeAsMcsYml(textWriter, groundedComponent);
            }
        }

        public virtual async Task<(ImmutableArray<WorkflowResponse>, CloudFlowMetadata)> UpsertWorkflowForAgentAsync(DirectoryPath workspaceFolder, DataverseClient dataverseClient, Guid? agentId, CancellationToken cancellationToken)
        {
            var cloudFlowDefinitions = new List<CloudFlowDefinition>();
            var workflows = new List<WorkflowMetadata>();
            ImmutableArray<ConnectionReference> connectionReferences = ImmutableArray<ConnectionReference>.Empty;
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

                        var clientDataJson = await File.ReadAllTextAsync(jsonFile, Encoding.UTF8, cancellationToken);
                        var yamlText = await File.ReadAllTextAsync(metadataFile, Encoding.UTF8, cancellationToken);
                        var deserializer = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).Build();
                        var metadata = deserializer.Deserialize<WorkflowMetadata>(yamlText);
                        metadata.ClientData = clientDataJson;
                        workflows.Add(metadata);

                        WorkflowResponse workflowResponse = await dataverseClient.UpdateWorkflowAsync(agentId.Value, metadata, cancellationToken).ConfigureAwait(false);

                        var (cloudFlowDefinition, _) = GetFlowDefinition(metadata);
                        cloudFlowDefinitions.Add(cloudFlowDefinition);
                        workflowResponseBuilder.Add(workflowResponse);
                    }

                    connectionReferences = await GetConnectionReferenceFromLogicalNamesAsync(GetConnectionReferenceLogicalNamesFromFlows(workflows), dataverseClient, cancellationToken);
                }
            }

            return (workflowResponseBuilder.ToImmutable(), new CloudFlowMetadata
            {
                Workflows = cloudFlowDefinitions.ToImmutableArray(),
                ConnectionReferences = connectionReferences
            });
        }

        public async Task<CloudFlowMetadata> GetWorkflowsAsync(DirectoryPath workspaceFolder, DataverseClient dataverseClient, Guid? agentId, IFileAccessor fileAccessor, CancellationToken cancellationToken)
        {
            var cloudFlowDefinitions = new List<CloudFlowDefinition>();
            var workflows = new List<WorkflowMetadata>();
            ImmutableArray<ConnectionReference> connectionReferences = ImmutableArray<ConnectionReference>.Empty;

            try
            {
                var remote = await dataverseClient.DownloadAllWorkflowsForAgentAsync(agentId, cancellationToken);
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

                        await using (var jsonStream = fileAccessor.OpenWrite(workflowJsonTmp))
                        await using (var writer = new StreamWriter(jsonStream, Encoding.UTF8))
                        {
                            await writer.WriteAsync(jsonString);
                        }
                        fileAccessor.Replace(workflowJsonTmp, workflowJson);

                        var workflowMetadata = new AgentFilePath($"{workflowFolder}/metadata.yml");
                        var workflowMetadataTmp = new AgentFilePath($"{workflowFolder}/metadata.yml.tmp");
                        var serializer = new SerializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).Build();

                        await using (var stream = fileAccessor.OpenWrite(workflowMetadataTmp))
                        await using (var writer = new StreamWriter(stream, Encoding.UTF8))
                        {
                            serializer.Serialize(writer, workflow);
                        }
                        fileAccessor.Replace(workflowMetadataTmp, workflowMetadata);
                    }
                }

                connectionReferences = await GetConnectionReferenceFromLogicalNamesAsync(GetConnectionReferenceLogicalNamesFromFlows(workflows), dataverseClient, cancellationToken);

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
                _logger.LogError($"Failed to download workflows for agent {agentId}. Exception: {ex.Message}");
            }

            return new CloudFlowMetadata
            {
                Workflows = cloudFlowDefinitions.ToImmutableArray(),
                ConnectionReferences = connectionReferences
            };
        }

        private async Task<ImmutableArray<Change>> GetLocalWorkflowChangesAsync(DirectoryPath workspaceFolder, DataverseClient dataverseClient, Guid? agentId, IFileAccessor fileAccessor, DefinitionBase originalDefinition, CancellationToken cancellationToken)
        {
            var localContent = await GetLocalWorkflowContentAsync(workspaceFolder, dataverseClient, agentId, fileAccessor, cancellationToken);
            var originalContent = await GetOriginalWorkflowContentAsync(originalDefinition, dataverseClient, cancellationToken);
            return ComputeWorkflowChanges(originalContent, localContent, isLocal: true);
        }

        private async Task<ImmutableArray<Change>> GetRemoteWorkflowChangesAsync(DataverseClient dataverseClient, Guid? agentId, DefinitionBase originalDefinition, CancellationToken cancellationToken)
        {
            var remoteContent = await GetRemoteWorkflowContentAsync(dataverseClient, agentId, cancellationToken);
            var originalContent = await GetOriginalWorkflowContentAsync(originalDefinition, dataverseClient, cancellationToken);
            return ComputeWorkflowChanges(originalContent, remoteContent, isLocal: false);
        }

        private async Task<CloudFlowMetadata> GetLocalWorkflowContentAsync(DirectoryPath workspaceFolder, DataverseClient dataverseClient, Guid? agentId, IFileAccessor fileAccessor, CancellationToken cancellationToken)
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

                var yaml = await File.ReadAllTextAsync(metadataFile, cancellationToken);
                var json = await File.ReadAllTextAsync(jsonFile, cancellationToken);
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
                ConnectionReferences = await GetConnectionReferenceFromLogicalNamesAsync(GetConnectionReferenceLogicalNamesFromFlows(workflows).ToImmutableArray(), dataverseClient, cancellationToken)
            };
        }

        private async Task<CloudFlowMetadata> GetRemoteWorkflowContentAsync(DataverseClient dataverseClient, Guid? agentId, CancellationToken cancellationToken)
        {
            var cloudFlowDefinitions = new List<CloudFlowDefinition>();
            var workflows = new List<WorkflowMetadata>();
            var remote = await dataverseClient.DownloadAllWorkflowsForAgentAsync(agentId, cancellationToken);

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
                ConnectionReferences = await GetConnectionReferenceFromLogicalNamesAsync(GetConnectionReferenceLogicalNamesFromFlows(workflows).ToImmutableArray(), dataverseClient, cancellationToken)
            };
        }

        private async Task<CloudFlowMetadata> GetOriginalWorkflowContentAsync(DefinitionBase definitionBase, DataverseClient dataverseClient, CancellationToken cancellationToken)
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
                ConnectionReferences = await GetConnectionReferenceFromLogicalNamesAsync(allLogicalNames.ToImmutableArray(), dataverseClient, cancellationToken)
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

        private async Task<ImmutableArray<ConnectionReference>> GetConnectionReferenceFromLogicalNamesAsync(IEnumerable<string> logicalNames, DataverseClient dataverseClient, CancellationToken cancellationToken)
        {
            ImmutableArray<ConnectionReference> references = ImmutableArray<ConnectionReference>.Empty;

            if (logicalNames.Any())
            {
                var result = await dataverseClient.GetConnectionReferencesByLogicalNamesAsync(logicalNames, cancellationToken);
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
            ImmutableArray<string> workflowConnectionNames = ImmutableArray<string>.Empty;
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
            JsonElement current = root;
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
            return jsonType.ToLowerInvariant() switch
            {
                "string" => DataType.String,
                "boolean" => DataType.Boolean,
                "number" => DataType.Number,
                "integer" => DataType.Number,
                "date" => DataType.DateTime,
                _ => DataType.String
            };
        }
    }
}