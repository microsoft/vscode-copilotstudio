// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.CopilotStudio.Sync.Copy;

internal sealed class CopyIdentityAuditor
{
    private const int ComponentPrefixProbeCount = 5;

    private readonly WorkspaceSynchronizer _synchronizer;

    public CopyIdentityAuditor(WorkspaceSynchronizer synchronizer)
    {
        _synchronizer = synchronizer ?? throw new ArgumentNullException(nameof(synchronizer));
    }

    public async Task<CopyAuditReport> AuditWorkspaceAsync(CopyPreparationRequest request, IReadOnlyList<CopyIdentityMapping> identities, CancellationToken cancellationToken)
    {
        var blockers = new List<CopyAuditFinding>();

        if (request.IsWorkflowCopy)
        {
            var copy = await WorkspaceCopyTransform.ReadStandaloneWorkflowAsync(WorkflowWorkspace.OpenWorkspace(request.WorkspacePath), blockers, cancellationToken).ConfigureAwait(false);

            if (copy is not null && !string.Equals(copy.Name, request.DisplayName, StringComparison.Ordinal))
            {
                blockers.Add(new CopyAuditFinding(CopyAuditCodes.LeftoverSourceIdentity, $"The copy is still named '{copy.Name}', not '{request.DisplayName}': the source's metadata.yml writes the name in a form the copy cannot rewrite. Write it on one line and copy again."));
            }

            if (copy is { StateCode: not (null or 0) } or { StatusCode: not (null or 1) })
            {
                blockers.Add(new CopyAuditFinding(CopyAuditCodes.LeftoverSourceIdentity, "The copy is not a draft (stateCode 0, statusCode 1): the source's metadata.yml writes the state in a form the copy cannot rewrite. Write it on one line and copy again."));
            }

            AuditTypedIdentities(request.WorkspacePath, identities, null, blockers, cancellationToken);

            return new CopyAuditReport { Blockers = blockers.Distinct().ToArray() };
        }

        var workspace = await ReadWorkspaceAsync(request.WorkspacePath, validate: true, checkKnowledgeFiles: true, blockers, cancellationToken).ConfigureAwait(false);
        var componentSchemas = workspace.ComponentSchemas.Distinct(StringComparer.Ordinal).OrderBy(schema => schema, StringComparer.Ordinal).ToArray();
        var (_, sourceSchema, destinationSchema) = SchemaMappingOf(identities);
        var budget = SchemaNameBudget(componentSchemas, destinationSchema);

        if (workspace.Agent is { } agent && AgentNameValidator.FindProblem(agent.DisplayName) is { } problem)
        {
            blockers.Add(new CopyAuditFinding(CopyAuditCodes.AgentNameUnsupported, $"The agent's display name {problem}.", WorkspaceCopyTransform.SettingsFileName));
        }

        if (destinationSchema.Length > SchemaNameValidator.MaximumLength)
        {
            blockers.Add(new CopyAuditFinding(CopyAuditCodes.SchemaTooLong, $"The schema name {destinationSchema} is longer than {SchemaNameValidator.MaximumLength} characters."));
        }

        foreach (var duplicate in workspace.ComponentSchemas.GroupBy(schema => schema, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
        {
            blockers.Add(new CopyAuditFinding(CopyAuditCodes.Unreadable, $"The workspace defines the component schema {duplicate.Key} {duplicate.Count()} times, so a push would create it more than once and Dataverse would refuse the copy."));
        }

        foreach (var schema in componentSchemas)
        {
            if (WorkspaceCopyTransform.IsSourceRooted(schema, sourceSchema))
            {
                blockers.Add(new CopyAuditFinding(CopyAuditCodes.LeftoverSourceIdentity, $"The component {schema} still uses the source agent's schema; deploying it would collide with the source's component."));
            }
            else if (!WorkspaceCopyTransform.IsSourceRooted(schema, destinationSchema))
            {
                blockers.Add(new CopyAuditFinding(CopyAuditCodes.ForeignComponentSchema, $"The component {schema} does not derive from the new schema {destinationSchema}, so the copy cannot give it an identity of its own."));
            }

            if (schema.Length > SchemaNameValidator.MaximumLength)
            {
                blockers.Add(new CopyAuditFinding(CopyAuditCodes.SchemaTooLong, $"The component schema {schema} is longer than {SchemaNameValidator.MaximumLength} characters. {(budget > 0 ? $"Choose a schema name of at most {budget} characters." : "Shorten the component's name; no schema name leaves it room.")}"));
            }
        }

        AuditTypedIdentities(request.WorkspacePath, identities, sourceSchema, blockers, cancellationToken);

        return new CopyAuditReport
        {
            Blockers = blockers.Distinct().ToArray(),
            ComponentSchemaNames = componentSchemas,
        };
    }

    internal static int SchemaNameBudget(IEnumerable<string> componentSchemas, string rootSchema) => SchemaNameValidator.MaximumLength - componentSchemas.Where(schema => WorkspaceCopyTransform.IsSourceRooted(schema, rootSchema)).Select(schema => schema.Length - rootSchema.Length).DefaultIfEmpty(0).Max();

    public async Task<CopyAuditReport> AuditEnvironmentAsync(CopyPreparationRequest request, IReadOnlyList<CopyIdentityMapping> identities, ICopyDataverseClient client, CancellationToken cancellationToken)
    {
        var blockers = new List<CopyAuditFinding>();

        if (!request.IsWorkflowCopy)
        {
            var destinationSchema = SchemaMappingOf(identities).Destination;
            var schemaOwner = await client.GetAgentIdBySchemaNameAsync(destinationSchema, cancellationToken).ConfigureAwait(false);

            if (schemaOwner != Guid.Empty)
            {
                blockers.Add(new CopyAuditFinding(CopyAuditCodes.IdentityTaken, $"An agent with the schema name {destinationSchema} already exists in the environment ({schemaOwner}). Copy the agent again with another schema name."));
            }
            else
            {
                var prefixed = await client.FindComponentSchemaNamesAsync(destinationSchema + ".", ComponentPrefixProbeCount, cancellationToken).ConfigureAwait(false);

                if (prefixed.Count > 0)
                {
                    blockers.Add(new CopyAuditFinding(CopyAuditCodes.IdentityTaken, $"Components named under {destinationSchema} already exist in the environment (for example {prefixed[0]}). Copy the agent again with another schema name."));
                }
            }
        }
        else if ((await client.FindCloudFlowsNamedAsync(request.DisplayName, cancellationToken).ConfigureAwait(false)).FirstOrDefault() is { } namesake)
        {
            blockers.Add(new CopyAuditFinding(CopyAuditCodes.IdentityTaken, $"The environment already has a workflow named '{namesake.Name}' ({namesake.WorkflowId}). Copy the workflow again under another name."));
        }

        var advice = request.IsWorkflowCopy ? "Copy the workflow again." : "Copy the agent again.";
        await AuditGuidsAsync(identities, CopyIdentityKinds.Workflow, "workflow", client.FindExistingWorkflowIdsAsync, advice, blockers, cancellationToken).ConfigureAwait(false);
        await AuditGuidsAsync(identities, CopyIdentityKinds.AIModel, "AI prompt", client.FindExistingAIModelIdsAsync, advice, blockers, cancellationToken).ConfigureAwait(false);
        await AuditGuidsAsync(identities, CopyIdentityKinds.Connector, "custom connector", client.FindExistingConnectorIdsAsync, advice, blockers, cancellationToken).ConfigureAwait(false);
        await AuditConnectorInternalIdsAsync(identities, client, blockers, cancellationToken).ConfigureAwait(false);
        await AuditConnectionReferencesAsync(identities, client, request.IsWorkflowCopy ? advice : "Copy the agent again with another schema name.", blockers, cancellationToken).ConfigureAwait(false);

        return new CopyAuditReport { Blockers = blockers };
    }

    internal async Task<(IReadOnlyList<string> ComponentSchemas, BotEntity? Agent)> ReadWorkspaceAsync(string workspaceRoot, bool validate, bool checkKnowledgeFiles, List<CopyAuditFinding> blockers, CancellationToken cancellationToken)
    {
        var folder = new DirectoryPath(workspaceRoot.Replace('\\', '/'));
        BotEntity? agent = null;

        try
        {
            agent = _synchronizer.IsSyncInfoAvailable(folder) ? null : CliAgentBotEntityReader.Read(WorkflowWorkspace.OpenWorkspace(workspaceRoot));
            var definition = await _synchronizer.ReadWorkspaceDefinitionAsync(folder, cancellationToken, checkKnowledgeFiles, diagnostics: null, validate, agent is null ? null : new BotDefinition(entity: agent)).ConfigureAwait(false);

            return (definition.Components.Where(component => !component.ParentBotComponentCollectionId.HasValue).Select(component => component.SchemaNameString).Where(schema => !string.IsNullOrEmpty(schema)).ToArray(), (definition as BotDefinition)?.Entity);
        }
        catch (WorkspaceValidationException failure)
        {
            blockers.AddRange(failure.Diagnostics.Select(diagnostic => new CopyAuditFinding(CopyAuditCodes.Unreadable, diagnostic.ToString(), diagnostic.FilePath)));

            return (Array.Empty<string>(), agent);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            blockers.Add(new CopyAuditFinding(CopyAuditCodes.Unreadable, $"The workspace could not be read as a push reads it: {failure.Message}"));

            return (Array.Empty<string>(), agent);
        }
    }

    private static void AuditTypedIdentities(string workspaceRoot, IReadOnlyList<CopyIdentityMapping> identities, string? sourceSchema, List<CopyAuditFinding> blockers, CancellationToken cancellationToken)
    {
        var replacedReferences = new HashSet<string>(SourceIdentities(identities, CopyIdentityKinds.ConnectionReference), StringComparer.OrdinalIgnoreCase);
        var replacedWorkflows = GuidSet(SourceIdentities(identities, CopyIdentityKinds.Workflow));
        var replacedModels = GuidSet(SourceIdentities(identities, CopyIdentityKinds.AIModel));
        var replacedConnectors = new HashSet<string>(SourceIdentities(identities, CopyIdentityKinds.ConnectorInternalId), StringComparer.OrdinalIgnoreCase);
        var workflows = WorkflowWorkspace.FindAll(workspaceRoot);
        var localWorkflows = new HashSet<Guid>(workflows.Select(workflow => workflow.WorkflowId));
        var localModels = new HashSet<Guid>(AgentWorkspaceResources.PromptFolders(workspaceRoot).Select(prompt => prompt.ModelId));

        foreach (var leftover in workflows.Where(workflow => replacedWorkflows.Contains(workflow.WorkflowId)))
        {
            blockers.Add(new CopyAuditFinding(CopyAuditCodes.LeftoverSourceIdentity, $"The workflow folder still names the source's workflow {leftover.WorkflowId} instead of its copy.", $"{WorkflowWorkspace.WorkflowsFolderName}/{leftover.FolderName}"));
        }

        foreach (var relative in (sourceSchema is null ? Enumerable.Empty<string>() : WorkspaceCopyTransform.ContentYamlFiles(workspaceRoot)).Concat(AgentWorkspaceResources.DeclarationFiles(workspaceRoot)).Concat(CopiedWorkflowFiles(workflows, WorkflowWorkspace.MetadataFileName)).Distinct(StringComparer.Ordinal))
        {
            Visit(relative, () => CopyTextEdits.VisitYaml(relative, WorkspaceCopyTransform.ReadText(Path.Combine(workspaceRoot, relative), out _), scalar => AuditScalar(scalar, relative)));
        }

        foreach (var relative in CopiedWorkflowFiles(workflows, WorkflowWorkspace.DefinitionFileName))
        {
            Visit(relative, () => CopyTextEdits.VisitJson(File.ReadAllBytes(Path.Combine(workspaceRoot, relative)), json => AuditDefinitionString(json, relative)));
        }

        void Visit(string relative, Action audit)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                audit();
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                blockers.Add(new CopyAuditFinding(CopyAuditCodes.Unreadable, $"The file could not be parsed: {failure.Message}", relative));
            }
        }

        void AuditDefinitionString(CopyJsonString json, string relative)
        {
            if (json.IsPropertyName)
            {
                return;
            }

            if (Guid.TryParse(json.Value, out var modelId) && replacedModels.Contains(modelId))
            {
                blockers.Add(new CopyAuditFinding(CopyAuditCodes.LeftoverSourceIdentity, $"The workflow definition still calls the source's AI prompt {modelId} instead of its copy.", relative));
            }
            else if (string.Equals(json.OwnerProperty, WorkspaceCopyTransform.ConnectionReferenceLogicalNameKey, StringComparison.Ordinal) && replacedReferences.Contains(json.Value))
            {
                blockers.Add(new CopyAuditFinding(CopyAuditCodes.LeftoverSourceIdentity, $"The workflow definition still names the source's connection reference {json.Value} instead of its copy.", relative));
            }
        }

        void AuditScalar(CopyYamlScalar scalar, string relative)
        {
            var value = scalar.Value;

            if (value.Length == 0)
            {
                return;
            }

            if (sourceSchema is not null && WorkspaceCopyTransform.IsSourceRooted(value, sourceSchema))
            {
                blockers.Add(new CopyAuditFinding(CopyAuditCodes.LeftoverSourceIdentity, $"The field {scalar.Key} still names the source agent's identity {value}.", relative));
                return;
            }

            if (WorkspaceCopyTransform.ConnectionReferenceKeys.Contains(scalar.Key) || (scalar.InSequence && string.Equals(scalar.Key, "connectionReferences", StringComparison.Ordinal)))
            {
                if (replacedReferences.Contains(value))
                {
                    blockers.Add(new CopyAuditFinding(CopyAuditCodes.LeftoverSourceIdentity, $"The field {scalar.Key} still names the source's connection reference {value}.", relative));
                }

                return;
            }

            if (WorkspaceCopyTransform.WorkflowKeys.Contains(scalar.Key) && Guid.TryParse(value, out var workflowId) && !relative.StartsWith(WorkflowWorkspace.WorkflowsFolderName + "/", StringComparison.Ordinal))
            {
                if (replacedWorkflows.Contains(workflowId))
                {
                    blockers.Add(new CopyAuditFinding(CopyAuditCodes.LeftoverSourceIdentity, $"The field {scalar.Key} still calls the source's workflow {workflowId} instead of its copy.", relative));
                }
                else if (!localWorkflows.Contains(workflowId))
                {
                    blockers.Add(new CopyAuditFinding(CopyAuditCodes.UnresolvedReference, $"The field {scalar.Key} calls workflow {workflowId}, which is not a copied workflow.", relative));
                }

                return;
            }

            if (string.Equals(scalar.Key, WorkspaceCopyTransform.AIModelKey, StringComparison.Ordinal) && Guid.TryParse(value, out var modelId) && !relative.StartsWith(AiPromptLayout.FolderName + "/", StringComparison.Ordinal))
            {
                if (replacedModels.Contains(modelId))
                {
                    blockers.Add(new CopyAuditFinding(CopyAuditCodes.LeftoverSourceIdentity, $"The field {scalar.Key} still calls the source's AI prompt {modelId} instead of its copy.", relative));
                }
                else if (!localModels.Contains(modelId))
                {
                    blockers.Add(new CopyAuditFinding(CopyAuditCodes.UnresolvedReference, $"The field {scalar.Key} calls AI prompt {modelId}, which is not a copied prompt.", relative));
                }

                return;
            }

            if (string.Equals(scalar.Key, WorkspaceCopyTransform.ConnectorKey, StringComparison.Ordinal) && WorkspaceCopyTransform.ConnectorInternalIdOf(value) is { } internalId && replacedConnectors.Contains(internalId))
            {
                blockers.Add(new CopyAuditFinding(CopyAuditCodes.LeftoverSourceIdentity, $"The field {scalar.Key} still names the source's custom connector {internalId} instead of its copy.", relative));
            }
        }
    }

    private static IEnumerable<string> CopiedWorkflowFiles(IReadOnlyList<WorkflowFolder> workflows, string fileName) => workflows.Select(workflow => $"{WorkflowWorkspace.WorkflowsFolderName}/{workflow.FolderName}/{fileName}");

    private static HashSet<Guid> GuidSet(IEnumerable<string> values) => new(values.Select(value => Guid.TryParse(value, out var parsed) ? parsed : Guid.Empty).Where(value => value != Guid.Empty));

    private static CopyIdentityMapping SchemaMappingOf(IEnumerable<CopyIdentityMapping> identities) => OfKind(identities, CopyIdentityKinds.Schema).Single();

    private static IEnumerable<string> SourceIdentities(IEnumerable<CopyIdentityMapping> identities, string kind) => OfKind(identities, kind).Select(mapping => mapping.Source);

    private static IEnumerable<string> DestinationIdentities(IEnumerable<CopyIdentityMapping> identities, string kind) => OfKind(identities, kind).Select(mapping => mapping.Destination);

    private static IEnumerable<CopyIdentityMapping> OfKind(IEnumerable<CopyIdentityMapping> identities, string kind) => identities.Where(mapping => string.Equals(mapping.Kind, kind, StringComparison.Ordinal));

    private static async Task AuditGuidsAsync(IReadOnlyList<CopyIdentityMapping> identities, string kind, string label, Func<IEnumerable<Guid>, CancellationToken, Task<IReadOnlyCollection<Guid>>> findExisting, string advice, List<CopyAuditFinding> blockers, CancellationToken cancellationToken)
    {
        var owned = GuidSet(DestinationIdentities(identities, kind));

        if (owned.Count == 0)
        {
            return;
        }

        var existing = new HashSet<Guid>(await findExisting(owned.ToArray(), cancellationToken).ConfigureAwait(false));

        foreach (var taken in owned.Where(existing.Contains))
        {
            blockers.Add(new CopyAuditFinding(CopyAuditCodes.IdentityTaken, $"The {label} {taken} this copy would create already exists in the environment. {advice}"));
        }
    }

    private static async Task AuditConnectorInternalIdsAsync(IReadOnlyList<CopyIdentityMapping> identities, ICopyDataverseClient client, List<CopyAuditFinding> blockers, CancellationToken cancellationToken)
    {
        var destinations = DestinationIdentities(identities, CopyIdentityKinds.ConnectorInternalId).ToArray();

        if (destinations.Length == 0)
        {
            return;
        }

        if (await client.FindConnectorEnvironmentSuffixAsync(cancellationToken).ConfigureAwait(false) is not { } environmentSuffix)
        {
            blockers.Add(new CopyAuditFinding(CopyAuditCodes.UnsupportedResource, "The environment's custom connector suffix could not be read, so the identifiers this copy's custom connectors would receive cannot be confirmed. Prepare the copy again from a workspace connected to this environment."));
        }
        else
        {
            foreach (var destination in destinations.Where(destination => !string.Equals(CustomConnectorInternalId.EnvironmentSuffixOf(destination), environmentSuffix, StringComparison.Ordinal)))
            {
                blockers.Add(new CopyAuditFinding(CopyAuditCodes.UnsupportedResource, $"The copied custom connector {destination} was prepared for another environment; this environment would give it the suffix {environmentSuffix}, so its tools and connection references would not find it. Prepare the copy again for this environment."));
            }
        }

        var stablePrefixes = new HashSet<string>(destinations.Select(CustomConnectorInternalId.StablePrefixOf).OfType<string>(), StringComparer.OrdinalIgnoreCase);

        foreach (var taken in (await client.FindConnectorsByInternalIdPrefixesAsync(stablePrefixes, cancellationToken).ConfigureAwait(false)).Where(connector => stablePrefixes.Contains(CustomConnectorInternalId.StablePrefixOf(connector.ConnectorInternalId) ?? string.Empty)))
        {
            blockers.Add(new CopyAuditFinding(CopyAuditCodes.IdentityTaken, $"The custom connector {taken.ConnectorInternalId} ({taken.ConnectorId}) already uses the name a connector this copy needs. Copy the agent again."));
        }
    }

    private static async Task AuditConnectionReferencesAsync(IReadOnlyList<CopyIdentityMapping> identities, ICopyDataverseClient client, string advice, List<CopyAuditFinding> blockers, CancellationToken cancellationToken)
    {
        var owned = DestinationIdentities(identities, CopyIdentityKinds.ConnectionReference).ToArray();

        if (owned.Length == 0)
        {
            return;
        }

        var rows = await client.GetConnectionReferencesByLogicalNamesAsync(owned, cancellationToken).ConfigureAwait(false);
        var existing = new HashSet<string>(rows.Select(row => row.ConnectionReferenceLogicalName), StringComparer.OrdinalIgnoreCase);

        foreach (var name in owned.Where(existing.Contains))
        {
            blockers.Add(new CopyAuditFinding(CopyAuditCodes.IdentityTaken, $"The connection reference {name} this copy would create already exists in the environment. {advice}"));
        }
    }
}
