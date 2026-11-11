// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Text;
using System.Text.Json;
using Microsoft.Agents.ObjectModel;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.McsCore.Yaml;
using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.CopilotStudio.Sync.Copy;

internal sealed class WorkspaceCopyTransform
{
    internal const string SettingsFileName = "settings.mcs.yml";

    internal const string ClassicAgentFileName = "agent.mcs.yml";

    internal const string ClassicConnectionReferencesFileName = "connectionreferences.mcs.yml";

    internal const string ConnectorApiPrefix = "/providers/Microsoft.PowerApps/apis/";

    internal const int MaximumConnectionReferenceNameLength = 200;

    internal static readonly HashSet<string> SchemaKeys = new(StringComparer.Ordinal) { "schemaName", "defaultSchemaName", "bundle", "manifestSchemaName", "dialog" };

    internal static readonly HashSet<string> WorkflowKeys = new(StringComparer.Ordinal) { "flowId", "workflowId" };

    internal static readonly HashSet<string> ConnectionReferenceKeys = new(StringComparer.Ordinal) { "connectionReference", "connectionReferenceLogicalName" };

    internal const string AIModelKey = "aIModelId";

    internal const string ConnectorKey = "connectorId";

    private const string WorkflowReferencesKey = "connectionReferences";

    private static readonly HashSet<string> DisplayNameKeys = new(StringComparer.Ordinal) { McsMetadata.ComponentNameKey, "modelDisplayName" };

    private const int MaximumResourceNameLength = 100;

    private const string NameSeparator = " - ";

    private const int MinimumRefreshedSuffixLength = 8;

    private const string HexAlphabet = "0123456789abcdef";

    private const int MaximumConnectorDisplayNameLength = 30;

    private const int ConnectorCopyMarkerLength = 6;

    private static readonly HashSet<string> LinkedDefinitionKinds = new(StringComparer.OrdinalIgnoreCase) { "$kind", "entity", "components", "environmentVariables", "flows", "connectionReferences", "connectorDefinitions", "aIModelDefinitions", "componentCollections", "connectedAgentDefinitions" };

    private readonly CopyPreparationRequest _request;
    private readonly string _root;
    private readonly string _destinationSchema;
    private readonly List<CopyAuditFinding> _blockers = new();
    private readonly List<CopyIdentityMapping> _identities = new();
    private readonly SortedSet<string> _rewritten = new(StringComparer.Ordinal);
    private readonly List<CopyIdentityMapping> _renamed = new();
    private readonly Dictionary<Guid, WorkflowCopy> _workflows = new();
    private readonly Dictionary<Guid, PromptCopy> _prompts = new();
    private readonly Dictionary<Guid, ConnectorCopy> _connectors = new();
    private readonly Dictionary<string, ConnectorCopy> _connectorCopiesByStablePrefix = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _duplicateConnectorFolders = new();
    private readonly Dictionary<string, string> _connectorInternalIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _connectionReferenceRenames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _declarationRenames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _declarationAdditions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _allocatedReferenceNames = new(StringComparer.OrdinalIgnoreCase);
    private string _sourceSchema = string.Empty;
    private string? _sourceDisplayName;
    private Task<string?>? _targetConnectorSuffix;

    private WorkspaceCopyTransform(CopyPreparationRequest request)
    {
        _request = request;
        _root = Path.GetFullPath(request.WorkspacePath);
        _destinationSchema = request.SchemaName;
    }

    /// <summary>Rewrites the staged workspace in place.</summary>
    /// <param name="request">What to copy, and into what.</param>
    /// <param name="cancellationToken">Cancels the transform between files.</param>
    /// <returns>What the transform wrote.</returns>
    /// <exception cref="CopyPreparationException">The workspace holds something the copy cannot handle; nothing was rewritten.</exception>
    public static Task<CopyPreparationResult> RunAsync(CopyPreparationRequest request, CancellationToken cancellationToken) => new WorkspaceCopyTransform(request).RunAsync(cancellationToken);

    internal static string? MapSchema(string value, string sourceSchema, string destinationSchema)
    {
        if (string.Equals(value, sourceSchema, StringComparison.Ordinal))
        {
            return destinationSchema;
        }

        return value.StartsWith(sourceSchema + ".", StringComparison.Ordinal) ? destinationSchema + value.Substring(sourceSchema.Length) : null;
    }

    internal static bool IsSourceRooted(string value, string sourceSchema) => string.Equals(value, sourceSchema, StringComparison.Ordinal) || value.StartsWith(sourceSchema + ".", StringComparison.Ordinal);

    internal static string? ConnectorInternalIdOf(string? connectorIdPath) => connectorIdPath is not null && connectorIdPath.StartsWith(ConnectorApiPrefix, StringComparison.OrdinalIgnoreCase) && connectorIdPath.Length > ConnectorApiPrefix.Length ? connectorIdPath.Substring(ConnectorApiPrefix.Length) : null;

    internal static IReadOnlyList<string> ContentYamlFiles(string root) => WorkspaceFiles(root).Where(IsContentYaml).OrderBy(path => path, StringComparer.Ordinal).ToArray();

    internal static IEnumerable<string> WorkspaceFiles(string root) => RelativeVisiblePaths(root, Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories));

    private static IEnumerable<string> RelativeVisiblePaths(string root, IEnumerable<string> paths)
    {
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        return paths.Select(path => Path.GetFullPath(path).Substring(prefix.Length).Replace('\\', '/')).Where(relative => !relative.Split('/').Any(segment => segment.StartsWith(".", StringComparison.Ordinal)));
    }

    internal static string ReadText(string path, out bool hasPreamble)
    {
        var bytes = File.ReadAllBytes(path);
        hasPreamble = CopyTextEdits.HasPreamble(bytes);

        return Encoding.UTF8.GetString(CopyTextEdits.WithoutPreamble(bytes));
    }

    private static bool IsContentYaml(string relative)
    {
        var leaf = relative.Substring(relative.LastIndexOf('/') + 1);

        if (!leaf.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) && !leaf.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (relative.StartsWith(WorkflowWorkspace.WorkflowsFolderName + "/", StringComparison.Ordinal) || relative.StartsWith(AiPromptLayout.FolderName + "/", StringComparison.Ordinal) || relative.StartsWith(CustomConnectorLayout.FolderName + "/", StringComparison.Ordinal) || relative.StartsWith(CliAgentConnectionsWriter.InfrastructureConnectionsFolder + "/", StringComparison.Ordinal))
        {
            return false;
        }

        return !string.Equals(relative, ClassicConnectionReferencesFileName, StringComparison.Ordinal) && !string.Equals(relative, AgentClassifier.WorkspaceLayoutMarkerFileName, StringComparison.Ordinal) && !string.Equals(relative, WorkflowWorkspace.ProjectFileName, StringComparison.Ordinal);
    }

    private async Task<CopyPreparationResult> RunAsync(CancellationToken cancellationToken)
    {
        ReadRoot();
        var inventory = TakeInventory(cancellationToken);

        if (_blockers.Count == 0)
        {
            Classify(inventory);
            await AllocateAsync(inventory, cancellationToken).ConfigureAwait(false);
        }

        if (_blockers.Count > 0)
        {
            throw new CopyPreparationException(_blockers.ToArray());
        }

        RewriteSettings();
        RewriteComponents(inventory, cancellationToken);
        RewriteDeclarations(inventory);
        RewriteWorkflows(cancellationToken);
        RewritePrompts();
        RewriteConnectors();
        RenameSourceRootedPaths();

        return new CopyPreparationResult
        {
            Identities = _identities.ToArray(),
            RewrittenFiles = _rewritten.ToArray(),
            RenamedPaths = _renamed.ToArray(),
            DraftWorkflows = _workflows.Values.Select(workflow => workflow.DestinationName).OrderBy(name => name, StringComparer.Ordinal).ToArray(),
        };
    }

    private void ReadRoot()
    {
        var settingsPath = Path.Combine(_root, SettingsFileName);

        if (!File.Exists(settingsPath))
        {
            _blockers.Add(new CopyAuditFinding(CopyAuditCodes.Unreadable, $"The workspace has no {SettingsFileName}, so it is not an agent workspace.", SettingsFileName));
            throw new CopyPreparationException(_blockers.ToArray());
        }

        var settings = ReadRootScalars(SettingsFileName);
        _sourceSchema = settings.TryGetValue("schemaName", out var schema) ? schema : string.Empty;
        _sourceDisplayName = settings.TryGetValue("displayName", out var displayName) ? displayName : null;

        if (_sourceSchema.Length == 0)
        {
            _blockers.Add(new CopyAuditFinding(CopyAuditCodes.Unreadable, "The workspace's settings do not name a schema.", SettingsFileName));
        }
        else if (!string.IsNullOrWhiteSpace(_request.SourceSchemaName) && !string.Equals(_request.SourceSchemaName, _sourceSchema, StringComparison.Ordinal))
        {
            _blockers.Add(new CopyAuditFinding(CopyAuditCodes.Unreadable, $"The workspace describes {_sourceSchema}, not the selected source {_request.SourceSchemaName}.", SettingsFileName));
        }

        if (string.Equals(_sourceSchema, _destinationSchema, StringComparison.OrdinalIgnoreCase))
        {
            _blockers.Add(new CopyAuditFinding(CopyAuditCodes.IdentityTaken, $"The new schema name {_destinationSchema} is the source's own schema name."));
        }

        if (AgentNameValidator.FindProblem(_request.DisplayName) is { } problem)
        {
            _blockers.Add(new CopyAuditFinding(CopyAuditCodes.AgentNameUnsupported, $"The new agent's display name {problem}."));
        }

        if (_blockers.Count > 0)
        {
            throw new CopyPreparationException(_blockers.ToArray());
        }
    }

    private Inventory TakeInventory(CancellationToken cancellationToken)
    {
        var inventory = new Inventory();

        foreach (var workflow in WorkflowWorkspace.FindAll(_root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folder = $"{WorkflowWorkspace.WorkflowsFolderName}/{workflow.FolderName}";

            if (ReadMetadata(workflow.MetadataPath, $"{folder}/{WorkflowWorkspace.MetadataFileName}") is not { } metadata)
            {
                continue;
            }

            var name = metadata.TryGetValue("name", out var value) && value is string text && text.Length > 0 ? text : workflow.FolderName;
            inventory.Workflows.Add(new WorkflowEntry(workflow.WorkflowId, workflow.FolderName, name));

            if (metadata.TryGetValue(WorkflowReferencesKey, out var listed) && listed is List<object?> references)
            {
                foreach (var reference in references.OfType<string>())
                {
                    inventory.WorkflowReferences.Add(reference);
                }
            }

            CollectWorkflowDefinitionReferences(inventory, File.ReadAllBytes(workflow.DefinitionPath), $"{folder}/{WorkflowWorkspace.DefinitionFileName}");
        }

        foreach (var prompt in AgentWorkspaceResources.PromptFolders(_root))
        {
            if (!File.Exists(Path.Combine(prompt.FolderPath, AiPromptLayout.MetadataFileName)) || ReadMetadata(Path.Combine(prompt.FolderPath, AiPromptLayout.MetadataFileName), $"{AiPromptLayout.FolderName}/{prompt.FolderName}/{AiPromptLayout.MetadataFileName}") is not { } metadata)
            {
                continue;
            }

            var promptPath = Path.Combine(prompt.FolderPath, AiPromptLayout.PromptFileName);
            var promptName = File.Exists(promptPath) ? AiPromptProjection.TryReadPromptName(ReadText(promptPath, out _)) : null;
            var name = promptName ?? (metadata.TryGetValue("name", out var value) && value is string text && text.Length > 0 ? text : prompt.FolderName);
            inventory.Prompts.Add(new PromptEntry(prompt.ModelId, prompt.FolderName, name, File.Exists(promptPath)));
        }

        foreach (var folder in AgentWorkspaceResources.ConnectorFolders(_root))
        {
            if (ReadConnector(folder) is { } connector)
            {
                inventory.Connectors.Add(connector);
            }
        }

        try
        {
            foreach (var declaration in AgentWorkspaceResources.DeclarationFiles(_root).SelectMany(relative => AgentWorkspaceResources.ReadDeclarations(_root, relative)))
            {
                inventory.Declarations.Add(declaration);
            }
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            _blockers.Add(new CopyAuditFinding(CopyAuditCodes.Unreadable, $"A connection reference declaration could not be parsed ({failure.Message}), so the copy's references cannot be declared."));
        }

        foreach (var relative in ContentYamlFiles(_root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            inventory.ContentFiles.Add(relative);

            try
            {
                CopyTextEdits.VisitYaml(relative, ReadText(Path.Combine(_root, relative), out _), scalar => CollectComponentReference(inventory, relative, scalar));
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                _blockers.Add(new CopyAuditFinding(CopyAuditCodes.Unreadable, $"The file could not be parsed ({failure.Message}), so its identities cannot be rewritten.", relative));
            }
        }

        return inventory;
    }

    private Dictionary<string, string> ReadRootScalars(string relative)
    {
        var scalars = new Dictionary<string, string>(StringComparer.Ordinal);

        CopyTextEdits.VisitYaml(relative, ReadText(Path.Combine(_root, relative), out _), scalar =>
        {
            if (scalar.AtRoot && !scalar.InSequence && !scalars.ContainsKey(scalar.Key))
            {
                scalars[scalar.Key] = scalar.Value;
            }
        });

        return scalars;
    }

    private Dictionary<string, object?>? ReadMetadata(string path, string relative)
    {
        try
        {
            return McsYamlReader.Parse(ReadText(path, out _));
        }
        catch (McsYamlFormatException failure)
        {
            _blockers.Add(new CopyAuditFinding(CopyAuditCodes.Unreadable, $"The metadata could not be parsed ({failure.Message}), so its identities cannot be rewritten.", relative));

            return null;
        }
    }

    private void CollectWorkflowDefinitionReferences(Inventory inventory, byte[] definition, string relative)
    {
        try
        {
            using var document = JsonDocument.Parse(CopyTextEdits.WithoutPreamble(definition).AsMemory(), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

            if (!document.RootElement.TryGetProperty("properties", out var properties) || !properties.TryGetProperty(WorkflowReferencesKey, out var references) || references.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (var reference in references.EnumerateObject())
            {
                if (!reference.Value.TryGetProperty("connection", out var connection) || !connection.TryGetProperty("connectionReferenceLogicalName", out var logicalName) || logicalName.GetString() is not { Length: > 0 } name)
                {
                    continue;
                }

                inventory.WorkflowReferences.Add(name);

                if (reference.Value.TryGetProperty("api", out var api) && api.TryGetProperty("name", out var apiName) && apiName.GetString() is { Length: > 0 } connectorInternalId)
                {
                    inventory.WorkflowReferenceConnectors[name] = ConnectorApiPrefix + connectorInternalId;
                }
            }
        }
        catch (JsonException failure)
        {
            _blockers.Add(new CopyAuditFinding(CopyAuditCodes.Unreadable, $"The workflow definition is not valid JSON ({failure.Message}), so its connection references cannot be rewritten.", relative));
        }
    }

    private static void CollectComponentReference(Inventory inventory, string relative, CopyYamlScalar scalar)
    {
        if (scalar.Value.Length == 0)
        {
            return;
        }

        if (ConnectionReferenceKeys.Contains(scalar.Key))
        {
            inventory.ComponentReferences.Add(scalar.Value);
            inventory.AddConnector(relative, ConnectorInternalIdOf(ConnectorOf(inventory, scalar.Value)));
        }
        else if (WorkflowKeys.Contains(scalar.Key) && Guid.TryParse(scalar.Value, out var workflowId))
        {
            inventory.AddReference(inventory.ReferencedWorkflows, relative, workflowId);
        }
        else if (string.Equals(scalar.Key, AIModelKey, StringComparison.Ordinal) && Guid.TryParse(scalar.Value, out var modelId))
        {
            inventory.AddReference(inventory.ReferencedModels, relative, modelId);
        }
    }

    private WorkspaceConnector? ReadConnector(string folder)
    {
        try
        {
            return AgentWorkspaceResources.ReadConnector(folder);
        }
        catch (JsonException failure)
        {
            _blockers.Add(new CopyAuditFinding(CopyAuditCodes.Unreadable, $"The custom connector metadata could not be parsed ({failure.Message}), so the connector cannot be copied.", $"{CustomConnectorLayout.FolderName}/{Path.GetFileName(folder)}/{CustomConnectorLayout.MetadataFileName}"));

            return null;
        }
    }

    private void Classify(Inventory inventory)
    {
        foreach (var prompt in inventory.Prompts.Where(prompt => !prompt.HasConfiguration))
        {
            _blockers.Add(new CopyAuditFinding(CopyAuditCodes.UnsupportedResource, $"The AI prompt '{prompt.Name}' has no prompt.json, so its definition cannot be copied.", $"{AiPromptLayout.FolderName}/{prompt.FolderName}"));
        }

        var localWorkflows = new HashSet<Guid>(inventory.Workflows.Select(workflow => workflow.Id));

        foreach (var missing in inventory.ReferencedWorkflows.Where(id => !localWorkflows.Contains(id)))
        {
            _blockers.Add(new CopyAuditFinding(CopyAuditCodes.UnresolvedReference, $"A component calls workflow {missing}, which the workspace does not hold, so it cannot be copied. Pull the source again."));
        }

        var localModels = new HashSet<Guid>(inventory.Prompts.Select(prompt => prompt.Id));

        foreach (var missing in inventory.ReferencedModels.Where(id => !localModels.Contains(id)))
        {
            _blockers.Add(new CopyAuditFinding(CopyAuditCodes.UnresolvedReference, $"A component calls AI prompt {missing}, which the workspace does not hold, so it cannot be copied. Pull the source again."));
        }

        foreach (var kind in UnclassifiedDefinitionKinds())
        {
            _blockers.Add(new CopyAuditFinding(CopyAuditCodes.UnsupportedResource, $"The source agent holds {kind}, which a copy cannot copy yet."));
        }
    }

    private IReadOnlyList<string> UnclassifiedDefinitionKinds()
    {
        if (string.IsNullOrWhiteSpace(_request.SourceDefinitionPath) || !File.Exists(_request.SourceDefinitionPath))
        {
            return Array.Empty<string>();
        }

        try
        {
            using var document = JsonDocument.Parse(CopyTextEdits.WithoutPreamble(File.ReadAllBytes(_request.SourceDefinitionPath)).AsMemory());

            return document.RootElement.ValueKind != JsonValueKind.Object ? Array.Empty<string>() : document.RootElement.EnumerateObject().Where(property => !LinkedDefinitionKinds.Contains(property.Name) && property.Value.ValueKind == JsonValueKind.Array && property.Value.GetArrayLength() > 0).Select(property => property.Name).ToArray();
        }
        catch (JsonException failure)
        {
            _blockers.Add(new CopyAuditFinding(CopyAuditCodes.Unreadable, $"The source's cloud definition could not be read ({failure.Message}), so the copy cannot confirm the source holds only resources a copy supports. Pull the source again."));

            return Array.Empty<string>();
        }
    }

    private async Task AllocateAsync(Inventory inventory, CancellationToken cancellationToken)
    {
        _identities.Add(new CopyIdentityMapping(CopyIdentityKinds.Schema, _sourceSchema, _destinationSchema));

        foreach (var workflow in inventory.Workflows)
        {
            var destinationId = Guid.NewGuid();
            var destinationName = ResourceName(workflow.Name);
            _workflows[workflow.Id] = new WorkflowCopy(workflow, destinationId, destinationName, WorkflowWorkspace.FolderNameFor(destinationName, destinationId));
            _identities.Add(new CopyIdentityMapping(CopyIdentityKinds.Workflow, workflow.Id.ToString("D"), destinationId.ToString("D")));
        }

        foreach (var prompt in inventory.Prompts)
        {
            var destinationId = Guid.NewGuid();
            var destinationName = ResourceName(prompt.Name);
            _prompts[prompt.Id] = new PromptCopy(prompt, destinationId, destinationName, $"{WorkspaceSynchronizer.SanitizeFolderSegment(destinationName)}-{destinationId:D}");
            _identities.Add(new CopyIdentityMapping(CopyIdentityKinds.AIModel, prompt.Id.ToString("D"), destinationId.ToString("D")));
        }

        var latestConnectors = LatestConnectorsByInternalId(inventory.Connectors);

        foreach (var connector in inventory.Connectors)
        {
            if (connector.InternalId is not { } internalId || string.IsNullOrWhiteSpace(internalId) || connector.Name is not { } name || string.IsNullOrWhiteSpace(name) || connector.DisplayName is not { } displayName || string.IsNullOrWhiteSpace(displayName))
            {
                _blockers.Add(new CopyAuditFinding(CopyAuditCodes.UnsupportedResource, $"The custom connector {connector.Id} does not record its name, display name, and internal identifier, so it cannot be copied.", $"{CustomConnectorLayout.FolderName}/{connector.FolderName}"));
                continue;
            }

            var latest = latestConnectors[internalId];

            if (latest != connector)
            {
                _duplicateConnectorFolders.Add($"{CustomConnectorLayout.FolderName}/{connector.FolderName}");
                continue;
            }

            if (CustomConnectorInternalId.ConformingSuffixOf(name, displayName, internalId) is not { } capturedSuffix)
            {
                _blockers.Add(new CopyAuditFinding(CopyAuditCodes.UnsupportedResource, $"The custom connector {connector.Id} has the internal identifier {connector.InternalId}, which is not the one the environment derives from its name prefix and display name, so the identifier a copy would receive cannot be known in advance.", $"{CustomConnectorLayout.FolderName}/{connector.FolderName}"));
                continue;
            }

            if (await TargetConnectorSuffixAsync(capturedSuffix, cancellationToken).ConfigureAwait(false) is not { } environmentSuffix)
            {
                continue;
            }

            var marker = SchemaNameGenerator.RandomString(ConnectorCopyMarkerLength, HexAlphabet);
            var destinationId = Guid.NewGuid();
            var destinationName = name + marker;
            var destinationDisplayName = ConnectorCopyDisplayName(displayName, marker);
            var destinationInternalId = CustomConnectorInternalId.Derive(destinationName, destinationDisplayName, environmentSuffix);
            var copy = new ConnectorCopy(connector, destinationId, destinationName, destinationDisplayName, destinationInternalId, $"{WorkspaceSynchronizer.SanitizeConnectorFolderName(destinationName)}-{destinationId:D}");
            _connectors[connector.Id] = copy;
            _connectorCopiesByStablePrefix[CustomConnectorInternalId.StablePrefixOf(internalId) ?? internalId] = copy;
            _connectorInternalIds[internalId] = destinationInternalId;
            _identities.Add(new CopyIdentityMapping(CopyIdentityKinds.Connector, connector.Id.ToString("D"), destinationId.ToString("D")));
            _identities.Add(new CopyIdentityMapping(CopyIdentityKinds.ConnectorInternalId, internalId, destinationInternalId));
        }

        AllocateConnectionReferences(inventory);
    }

    private void AllocateConnectionReferences(Inventory inventory)
    {
        var declared = new HashSet<string>(inventory.Declarations.Select(declaration => declaration.LogicalName), StringComparer.OrdinalIgnoreCase);
        var everyName = new HashSet<string>(declared.Concat(inventory.ComponentReferences).Concat(inventory.WorkflowReferences), StringComparer.OrdinalIgnoreCase);

        foreach (var name in everyName)
        {
            _allocatedReferenceNames.Add(name);
        }

        foreach (var name in everyName.OrderBy(name => name, StringComparer.Ordinal))
        {
            var destination = AllocateConnectionReferenceName(name);

            if (destination is null)
            {
                continue;
            }

            _identities.Add(new CopyIdentityMapping(CopyIdentityKinds.ConnectionReference, name, destination));
            _connectionReferenceRenames[name] = destination;

            if (declared.Contains(name))
            {
                _declarationRenames[name] = destination;
            }
            else
            {
                _declarationAdditions[name] = destination;
            }
        }

        foreach (var addition in _declarationAdditions.Keys.ToArray())
        {
            if (!declared.Contains(addition) && ConnectorOf(inventory, addition) is null)
            {
                _blockers.Add(new CopyAuditFinding(CopyAuditCodes.UnresolvedReference, $"The connection reference {addition} is used but not declared, and no workflow names its connector, so its copy cannot be declared."));
            }
        }
    }

    private string? AllocateConnectionReferenceName(string source)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var candidate = CandidateReferenceName(source);

            if (candidate.Length > MaximumConnectionReferenceNameLength)
            {
                _blockers.Add(new CopyAuditFinding(CopyAuditCodes.SchemaTooLong, $"The connection reference {source} would be renamed to {candidate}, which is longer than {MaximumConnectionReferenceNameLength} characters. Choose a shorter schema name."));

                return null;
            }

            if (_allocatedReferenceNames.Add(candidate))
            {
                return candidate;
            }
        }

        _blockers.Add(new CopyAuditFinding(CopyAuditCodes.IdentityTaken, $"No unique name could be allocated for the connection reference {source}."));

        return null;
    }

    private string CandidateReferenceName(string source)
    {
        var named = NameCopiedConnectors(source);

        if (MapSchema(named, _sourceSchema, _destinationSchema) is { } rerooted && !_allocatedReferenceNames.Contains(rerooted))
        {
            return rerooted;
        }

        var dot = named.IndexOf('.');

        return dot > 0 ? _destinationSchema + RefreshTrailingSuffix(named.Substring(dot)) : RefreshTrailingSuffix(named);
    }

    private string NameCopiedConnectors(string referenceName) => string.Join(".", referenceName.Split('.').Select(segment => CopiedConnectorOf(segment)?.DestinationInternalId ?? segment));

    private ConnectorCopy? CopiedConnectorOf(string internalId) => CustomConnectorInternalId.StablePrefixOf(internalId) is { } stablePrefix && _connectorCopiesByStablePrefix.TryGetValue(stablePrefix, out var copy) ? copy : null;

    private static string RefreshTrailingSuffix(string value)
    {
        var separator = value.LastIndexOfAny(new[] { '.', '_', '-' });
        var tail = separator >= 0 ? value.Substring(separator + 1) : string.Empty;

        if (tail.Length >= 4 && tail.All(Uri.IsHexDigit))
        {
            return value.Substring(0, separator + 1) + SchemaNameGenerator.RandomString(Math.Max(tail.Length, MinimumRefreshedSuffixLength), HexAlphabet);
        }

        return value + "_" + SchemaNameGenerator.RandomString(MinimumRefreshedSuffixLength, HexAlphabet);
    }

    private async Task<string?> TargetConnectorSuffixAsync(string capturedSuffix, CancellationToken cancellationToken)
    {
        if (_request.CapturedInTargetEnvironment)
        {
            return capturedSuffix;
        }

        _targetConnectorSuffix ??= ReadTargetConnectorSuffixAsync(cancellationToken);

        return await _targetConnectorSuffix.ConfigureAwait(false);
    }

    private async Task<string?> ReadTargetConnectorSuffixAsync(CancellationToken cancellationToken)
    {
        var suffix = _request.TargetEnvironment is { } environment ? await environment.FindConnectorEnvironmentSuffixAsync(cancellationToken).ConfigureAwait(false) : null;

        if (suffix is null)
        {
            _blockers.Add(new CopyAuditFinding(CopyAuditCodes.UnsupportedResource, "The custom connector suffix of the environment the copy deploys to could not be read, so the identifier a copied custom connector would receive cannot be known in advance. Copy from a workspace connected to that environment."));
        }

        return suffix;
    }

    private static Dictionary<string, WorkspaceConnector> LatestConnectorsByInternalId(IEnumerable<WorkspaceConnector> connectors) => connectors.Where(connector => !string.IsNullOrWhiteSpace(connector.InternalId)).GroupBy(connector => connector.InternalId ?? string.Empty, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.OrderByDescending(connector => connector.Version ?? long.MinValue).ThenBy(connector => connector.FolderName, StringComparer.Ordinal).First(), StringComparer.OrdinalIgnoreCase);

    private static string ConnectorCopyDisplayName(string sourceDisplayName, string marker)
    {
        var permitted = new string(sourceDisplayName.Select(character => AsciiCharacters.IsLetterOrDigit(character) || character is ' ' or '-' or '_' ? character : ' ').ToArray()).Trim(' ', '-', '_');
        var room = MaximumConnectorDisplayNameLength - marker.Length - 1;
        var stem = permitted.Length > room ? permitted.Substring(0, room).TrimEnd(' ', '-', '_') : permitted;

        return stem.Length == 0 ? marker : $"{stem} {marker}";
    }

    private string ResourceName(string sourceName) => WithinResourceNameLength(_request.DisplayName + NameSeparator + sourceName);

    private static string WithinResourceNameLength(string name) => name.Length <= MaximumResourceNameLength ? name : name.Substring(0, MaximumResourceNameLength).TrimEnd();

    private static string? ConnectorOf(Inventory inventory, string referenceName)
    {
        var declared = inventory.Declarations.FirstOrDefault(declaration => string.Equals(declaration.LogicalName, referenceName, StringComparison.OrdinalIgnoreCase))?.ConnectorId;

        if (!string.IsNullOrWhiteSpace(declared))
        {
            return declared;
        }

        return inventory.WorkflowReferenceConnectors.TryGetValue(referenceName, out var fromWorkflow) ? fromWorkflow : null;
    }

    private string? MapConnectorPath(string value)
    {
        if (ConnectorInternalIdOf(value) is { } internalId && _connectorInternalIds.TryGetValue(internalId, out var destination))
        {
            return value.Substring(0, value.Length - internalId.Length) + destination;
        }

        return null;
    }

    private string? MapGuid(string value, Func<Guid, Guid?> map) => Guid.TryParse(value, out var source) && map(source) is { } destination ? destination.ToString("D") : null;

    private void RewriteSettings()
    {
        RewriteYamlFile(SettingsFileName, scalar => scalar.AtRoot && string.Equals(scalar.Key, "displayName", StringComparison.Ordinal) ? _request.DisplayName : scalar.AtRoot && string.Equals(scalar.Key, "schemaName", StringComparison.Ordinal) ? _destinationSchema : MapComponentScalar(scalar), removeRootProperty: "publishedOn");
    }

    private void RewriteComponents(Inventory inventory, CancellationToken cancellationToken)
    {
        foreach (var relative in inventory.ContentFiles.Where(relative => !string.Equals(relative, SettingsFileName, StringComparison.Ordinal)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var renamedDisplayNames = RenamedDisplayNames(inventory, relative);
            var copiedConnectors = CopiedConnectorsOf(inventory, relative);
            RewriteYamlFile(relative, scalar => RenamedDisplayName(scalar, renamedDisplayNames, copiedConnectors) ?? MapComponentScalar(scalar));
        }
    }

    private static string? RenamedDisplayName(CopyYamlScalar scalar, Dictionary<string, string> renamedDisplayNames, IReadOnlyList<ConnectorCopy> copiedConnectors) => DisplayNameKeys.Contains(scalar.Key) && renamedDisplayNames.TryGetValue(scalar.Value, out var displayName) ? displayName : string.Equals(scalar.Key, McsMetadata.ComponentNameKey, StringComparison.Ordinal) ? ConnectorToolName(scalar.Value, copiedConnectors) : null;

    private static string? ConnectorToolName(string componentName, IReadOnlyList<ConnectorCopy> copiedConnectors)
    {
        foreach (var copy in copiedConnectors)
        {
            if (copy.Source.DisplayName is { } sourceDisplayName && componentName.StartsWith(sourceDisplayName + NameSeparator, StringComparison.Ordinal))
            {
                return WithinResourceNameLength(copy.DestinationDisplayName + componentName.Substring(sourceDisplayName.Length));
            }
        }

        return null;
    }

    private IReadOnlyList<ConnectorCopy> CopiedConnectorsOf(Inventory inventory, string relative) => inventory.ConnectorsByFile.TryGetValue(relative, out var internalIds) ? internalIds.Select(CopiedConnectorOf).OfType<ConnectorCopy>().ToArray() : Array.Empty<ConnectorCopy>();

    private Dictionary<string, string> RenamedDisplayNames(Inventory inventory, string relative)
    {
        var renamedDisplayNames = new Dictionary<string, string>(StringComparer.Ordinal);

        if (string.Equals(relative, ClassicAgentFileName, StringComparison.Ordinal) && _sourceDisplayName is { } sourceDisplayName)
        {
            renamedDisplayNames[sourceDisplayName] = _request.DisplayName;
        }

        if (inventory.ReferencesByFile.TryGetValue(relative, out var references))
        {
            foreach (var names in references.Select(CopiedResourceNames).OfType<(string SourceName, string DestinationName)>())
            {
                renamedDisplayNames[names.SourceName] = names.DestinationName;
            }
        }

        return renamedDisplayNames;
    }

    private (string SourceName, string DestinationName)? CopiedResourceNames(Guid resource) => _workflows.TryGetValue(resource, out var workflow) ? (workflow.Source.Name, workflow.DestinationName) : _prompts.TryGetValue(resource, out var prompt) ? (prompt.Source.Name, prompt.DestinationName) : null;

    private string? MapComponentScalar(CopyYamlScalar scalar)
    {
        if (SchemaKeys.Contains(scalar.Key))
        {
            return MapSchema(scalar.Value, _sourceSchema, _destinationSchema);
        }

        if (ConnectionReferenceKeys.Contains(scalar.Key))
        {
            return _connectionReferenceRenames.TryGetValue(scalar.Value, out var reference) ? reference : null;
        }

        if (WorkflowKeys.Contains(scalar.Key))
        {
            return MapGuid(scalar.Value, source => _workflows.TryGetValue(source, out var copy) ? copy.DestinationId : null);
        }

        if (string.Equals(scalar.Key, AIModelKey, StringComparison.Ordinal))
        {
            return MapGuid(scalar.Value, source => _prompts.TryGetValue(source, out var copy) ? copy.DestinationId : null);
        }

        return string.Equals(scalar.Key, ConnectorKey, StringComparison.Ordinal) ? MapConnectorPath(scalar.Value) : null;
    }

    private string? MapDeclarationScalar(CopyYamlScalar scalar, IReadOnlyDictionary<string, string> names)
    {
        if (ConnectionReferenceKeys.Contains(scalar.Key))
        {
            return names.TryGetValue(scalar.Value, out var renamed) ? renamed : null;
        }

        return string.Equals(scalar.Key, ConnectorKey, StringComparison.Ordinal) ? MapConnectorPath(scalar.Value) : null;
    }

    private void RewriteDeclarations(Inventory inventory)
    {
        var declarationFiles = inventory.Declarations.Select(declaration => declaration.File).Distinct(StringComparer.Ordinal).ToArray();
        var cliLayout = !File.Exists(Path.Combine(_root, ClassicConnectionReferencesFileName)) && (Directory.Exists(Path.Combine(_root, CliAgentConnectionsWriter.InfrastructureConnectionsFolder)) || File.Exists(Path.Combine(_root, AgentClassifier.WorkspaceLayoutMarkerFileName)));

        foreach (var relative in declarationFiles.Where(file => !string.Equals(file, ClassicConnectionReferencesFileName, StringComparison.Ordinal)))
        {
            var logicalName = inventory.Declarations.First(declaration => string.Equals(declaration.File, relative, StringComparison.Ordinal)).LogicalName;
            var text = ReadText(Path.Combine(_root, relative), out var hasPreamble);

            if (_declarationRenames.TryGetValue(logicalName, out var renamed))
            {
                var target = CliAgentConnectionsWriter.InfrastructureConnectionsFolder + "/" + CliAgentConnectionsWriter.ComputeConnectionFileName(renamed);
                WriteText(relative, CopyTextEdits.RewriteYaml(relative, text, scalar => MapDeclarationScalar(scalar, _declarationRenames)), hasPreamble);
                MovePath(relative, target);
            }

            if (_declarationAdditions.TryGetValue(logicalName, out var added))
            {
                var additions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [logicalName] = added };
                var target = CliAgentConnectionsWriter.InfrastructureConnectionsFolder + "/" + CliAgentConnectionsWriter.ComputeConnectionFileName(added);
                WriteText(target, CopyTextEdits.RewriteYaml(relative, text, scalar => MapDeclarationScalar(scalar, additions)), hasPreamble);
            }
        }

        var classicPath = Path.Combine(_root, ClassicConnectionReferencesFileName);
        var classicAdditions = new List<ConnectionReference>();

        foreach (var addition in _declarationAdditions.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var connector = MapConnectorPath(ConnectorOf(inventory, addition.Key) ?? string.Empty) ?? ConnectorOf(inventory, addition.Key) ?? string.Empty;
            var reference = new ConnectionReference(connectionReferenceLogicalName: addition.Value, connectionId: string.Empty, connectorId: connector);

            if (cliLayout && inventory.Declarations.All(declaration => !string.Equals(declaration.LogicalName, addition.Key, StringComparison.OrdinalIgnoreCase)))
            {
                WriteText(CliAgentConnectionsWriter.InfrastructureConnectionsFolder + "/" + CliAgentConnectionsWriter.ComputeConnectionFileName(addition.Value), ConnectionReferenceStore.Serialize(new[] { reference }), hasPreamble: false);
            }
            else if (!cliLayout)
            {
                classicAdditions.Add(reference);
            }
        }

        if (!File.Exists(classicPath) && classicAdditions.Count == 0)
        {
            return;
        }

        if (!File.Exists(classicPath))
        {
            WriteText(ClassicConnectionReferencesFileName, ConnectionReferenceStore.Serialize(classicAdditions), hasPreamble: true);
            return;
        }

        var classicText = CopyTextEdits.RewriteYaml(ClassicConnectionReferencesFileName, ReadText(classicPath, out var classicPreamble), scalar => MapDeclarationScalar(scalar, _declarationRenames));

        if (classicAdditions.Count > 0)
        {
            classicText = ConnectionReferenceStore.Serialize(ConnectionReferenceStore.ParseFlat(classicText).Concat(classicAdditions).ToList());
        }

        WriteText(ClassicConnectionReferencesFileName, classicText, classicPreamble);
    }

    private void RewriteWorkflows(CancellationToken cancellationToken)
    {
        foreach (var copy in _workflows.Values.OrderBy(copy => copy.Source.FolderName, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folder = $"{WorkflowWorkspace.WorkflowsFolderName}/{copy.Source.FolderName}";
            var metadata = $"{folder}/{WorkflowWorkspace.MetadataFileName}";
            var definition = $"{folder}/{WorkflowWorkspace.DefinitionFileName}";
            var destinationFolder = $"{WorkflowWorkspace.WorkflowsFolderName}/{copy.DestinationFolderName}";

            RewriteYamlFile(metadata, scalar => MapWorkflowMetadata(scalar, copy, destinationFolder));
            RewriteJsonFile(definition, json => MapWorkflowDefinition(json));
            MovePath(folder, destinationFolder);
        }
    }

    private string? MapWorkflowMetadata(CopyYamlScalar scalar, WorkflowCopy copy, string destinationFolder)
    {
        if (scalar.InSequence)
        {
            return string.Equals(scalar.Key, WorkflowReferencesKey, StringComparison.Ordinal) && _connectionReferenceRenames.TryGetValue(scalar.Value, out var reference) ? reference : null;
        }

        if (!scalar.AtRoot)
        {
            return null;
        }

        return scalar.Key switch
        {
            "workflowId" => copy.DestinationId.ToString("D"),
            "name" => copy.DestinationName,
            "jsonFileName" => $"{destinationFolder}/{WorkflowWorkspace.DefinitionFileName}",
            "stateCode" => "0",
            "statusCode" => "1",
            _ => null,
        };
    }

    private string? MapWorkflowDefinition(CopyJsonString json)
    {
        if (json.IsPropertyName)
        {
            return _connectorInternalIds.TryGetValue(json.Value, out var renamedKey) ? renamedKey : null;
        }

        if (string.Equals(json.OwnerProperty, "connectionReferenceLogicalName", StringComparison.Ordinal))
        {
            return _connectionReferenceRenames.TryGetValue(json.Value, out var reference) ? reference : null;
        }

        if (Guid.TryParse(json.Value, out var modelId) && _prompts.TryGetValue(modelId, out var prompt))
        {
            return prompt.DestinationId.ToString("D");
        }

        return _connectorInternalIds.TryGetValue(json.Value, out var internalId) ? internalId : MapConnectorPath(json.Value);
    }

    private void RewritePrompts()
    {
        foreach (var copy in _prompts.Values.OrderBy(copy => copy.Source.FolderName, StringComparer.Ordinal))
        {
            var folder = $"{AiPromptLayout.FolderName}/{copy.Source.FolderName}";
            RewriteYamlFile($"{folder}/{AiPromptLayout.MetadataFileName}", scalar => !scalar.AtRoot || scalar.InSequence ? null : scalar.Key switch
            {
                AIModelKey => copy.DestinationId.ToString("D"),
                "name" => copy.DestinationName,
                _ => null,
            });

            RewriteJsonFile($"{folder}/{AiPromptLayout.PromptFileName}", json => json.Depth == 1 && !json.IsPropertyName && string.Equals(json.OwnerProperty, "name", StringComparison.Ordinal) ? copy.DestinationName : null);
            MovePath(folder, $"{AiPromptLayout.FolderName}/{copy.DestinationFolderName}");
        }
    }

    private void RewriteConnectors()
    {
        foreach (var folder in _duplicateConnectorFolders)
        {
            Directory.Delete(Path.Combine(_root, folder), recursive: true);
        }

        foreach (var copy in _connectors.Values.OrderBy(copy => copy.Source.FolderName, StringComparer.Ordinal))
        {
            var folder = $"{CustomConnectorLayout.FolderName}/{copy.Source.FolderName}";
            var destinationFolder = $"{CustomConnectorLayout.FolderName}/{copy.DestinationFolderName}";

            RewriteJsonFile($"{folder}/{CustomConnectorLayout.MetadataFileName}", json => json.IsPropertyName || json.Depth != 1 ? null : json.OwnerProperty switch
            {
                "connectorid" => copy.DestinationId.ToString("D"),
                "name" => copy.DestinationName,
                "displayname" => copy.DestinationDisplayName,
                "connectorinternalid" => copy.DestinationInternalId,
                _ => json.Value.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase) ? destinationFolder + json.Value.Substring(folder.Length) : null,
            });

            MovePath(folder, destinationFolder);
        }
    }

    private void RenameSourceRootedPaths()
    {
        var excluded = new[] { WorkflowWorkspace.WorkflowsFolderName + "/", AiPromptLayout.FolderName + "/", CustomConnectorLayout.FolderName + "/", CliAgentConnectionsWriter.InfrastructureConnectionsFolder + "/" };
        var candidates = RelativeVisiblePaths(_root, Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories)).Where(relative => !excluded.Any(prefix => relative.StartsWith(prefix, StringComparison.Ordinal)) && IsSourceRooted(LeafOf(relative), _sourceSchema)).OrderByDescending(relative => relative.Count(character => character == '/')).ToArray();

        foreach (var relative in candidates)
        {
            var leaf = LeafOf(relative);
            var parent = relative.Length > leaf.Length ? relative.Substring(0, relative.Length - leaf.Length) : string.Empty;
            MovePath(relative, parent + _destinationSchema + leaf.Substring(_sourceSchema.Length));
        }
    }

    private static string LeafOf(string relative) => relative.Substring(relative.LastIndexOf('/') + 1);

    private void RewriteYamlFile(string relative, Func<CopyYamlScalar, string?> replace, string? removeRootProperty = null)
    {
        var path = Path.Combine(_root, relative);

        if (!File.Exists(path))
        {
            return;
        }

        var original = ReadText(path, out var hasPreamble);
        var rewritten = CopyTextEdits.RewriteYaml(relative, original, replace);

        if (removeRootProperty is not null)
        {
            rewritten = CopyTextEdits.RemoveRootProperty(rewritten, removeRootProperty);
        }

        if (!string.Equals(original, rewritten, StringComparison.Ordinal))
        {
            WriteText(relative, rewritten, hasPreamble);
        }
    }

    private void RewriteJsonFile(string relative, Func<CopyJsonString, string?> replace)
    {
        var path = Path.Combine(_root, relative);

        if (!File.Exists(path))
        {
            return;
        }

        var original = File.ReadAllBytes(path);
        var rewritten = CopyTextEdits.RewriteJson(original, replace);

        if (!ReferenceEquals(original, rewritten))
        {
            File.WriteAllBytes(path, rewritten);
            _rewritten.Add(relative);
        }
    }

    private void WriteText(string relative, string text, bool hasPreamble)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new InvalidOperationException($"The copied file {relative} must have a containing directory."));
        File.WriteAllText(path, text, new UTF8Encoding(hasPreamble));
        _rewritten.Add(relative);
    }

    private void MovePath(string relative, string destination)
    {
        if (string.Equals(relative, destination, StringComparison.Ordinal))
        {
            return;
        }

        var source = Path.Combine(_root, relative);
        var target = Path.Combine(_root, destination);

        if (File.Exists(target) || Directory.Exists(target))
        {
            throw new CopyPreparationException(new[] { new CopyAuditFinding(CopyAuditCodes.IdentityTaken, $"Renaming {relative} would overwrite {destination}.", relative) });
        }

        if (Directory.Exists(source))
        {
            Directory.Move(source, target);
            RenameTracked(relative + "/", destination + "/");
        }
        else
        {
            File.Move(source, target);
            RenameTracked(relative, destination);
        }

        _renamed.Add(new CopyIdentityMapping("path", relative, destination));
    }

    private void RenameTracked(string source, string destination)
    {
        foreach (var tracked in _rewritten.Where(path => path.StartsWith(source, StringComparison.Ordinal)).ToArray())
        {
            _rewritten.Remove(tracked);
            _rewritten.Add(destination + tracked.Substring(source.Length));
        }
    }

    private sealed class Inventory
    {
        public List<WorkflowEntry> Workflows { get; } = new();

        public List<PromptEntry> Prompts { get; } = new();

        public List<WorkspaceConnector> Connectors { get; } = new();

        public List<WorkspaceDeclaration> Declarations { get; } = new();

        public List<string> ContentFiles { get; } = new();

        public HashSet<string> ComponentReferences { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> WorkflowReferences { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, string> WorkflowReferenceConnectors { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<Guid> ReferencedWorkflows { get; } = new();

        public HashSet<Guid> ReferencedModels { get; } = new();

        public Dictionary<string, List<Guid>> ReferencesByFile { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, List<string>> ConnectorsByFile { get; } = new(StringComparer.Ordinal);

        public void AddReference(HashSet<Guid> referenced, string relative, Guid resource)
        {
            referenced.Add(resource);
            AddToFile(ReferencesByFile, relative, resource);
        }

        public void AddConnector(string relative, string? internalId)
        {
            if (internalId is not null)
            {
                AddToFile(ConnectorsByFile, relative, internalId);
            }
        }

        private static void AddToFile<TReference>(Dictionary<string, List<TReference>> referencesByFile, string relative, TReference reference)
        {
            if (!referencesByFile.TryGetValue(relative, out var references))
            {
                references = new List<TReference>();
                referencesByFile[relative] = references;
            }

            references.Add(reference);
        }
    }

    private sealed record WorkflowEntry(Guid Id, string FolderName, string Name);

    private sealed record PromptEntry(Guid Id, string FolderName, string Name, bool HasConfiguration);

    private sealed record WorkflowCopy(WorkflowEntry Source, Guid DestinationId, string DestinationName, string DestinationFolderName);

    private sealed record PromptCopy(PromptEntry Source, Guid DestinationId, string DestinationName, string DestinationFolderName);

    private sealed record ConnectorCopy(WorkspaceConnector Source, Guid DestinationId, string DestinationName, string DestinationDisplayName, string DestinationInternalId, string DestinationFolderName);
}
