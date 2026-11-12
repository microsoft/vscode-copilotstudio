// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.CopilotStudio.McsCore;

namespace Microsoft.CopilotStudio.Sync.Copy;

/// <summary>A flow, AI prompt, custom connector, or connection reference, named by its kind and identity.</summary>
/// <param name="Kind"><see cref="CopyIdentityKinds.Workflow"/>, <see cref="CopyIdentityKinds.AIModel"/>, <see cref="CopyIdentityKinds.Connector"/>, or <see cref="CopyIdentityKinds.ConnectionReference"/>.</param>
/// <param name="Identity">The resource's identifier, or the connection reference's logical name.</param>
public sealed record AgentWorkspaceResource(string Kind, string Identity);

/// <summary>Lists the flows, AI prompts, custom connectors, and connection references an agent workspace holds, and finds which of an agent's resources an environment holds.</summary>
public static class AgentWorkspaceResources
{
    /// <summary>Lists every flow, AI prompt, custom connector, and connection reference the workspace holds, reading no service.</summary>
    /// <param name="workspacePath">The agent workspace.</param>
    /// <returns>The resources: flows, then AI prompts, custom connectors, and connection references.</returns>
    /// <exception cref="InvalidDataException">A custom connector's metadata or a connection reference declaration could not be read.</exception>
    public static IReadOnlyList<AgentWorkspaceResource> List(string workspacePath)
    {
        var root = Path.GetFullPath(workspacePath);

        return WorkflowWorkspace.FindAll(root).Select(workflow => new AgentWorkspaceResource(CopyIdentityKinds.Workflow, workflow.WorkflowId.ToString("D"))).Concat(PromptFolders(root).Select(prompt => new AgentWorkspaceResource(CopyIdentityKinds.AIModel, prompt.ModelId.ToString("D")))).Concat(ConnectorFolders(root).Select(folder => Parsed(() => ReadConnector(folder), $"{CustomConnectorLayout.FolderName}/{Path.GetFileName(folder)}/{CustomConnectorLayout.MetadataFileName}")).OfType<WorkspaceConnector>().Select(connector => new AgentWorkspaceResource(CopyIdentityKinds.Connector, connector.Id.ToString("D")))).Concat(DeclarationFiles(root).SelectMany(relative => Parsed(() => ReadDeclarations(root, relative), relative)).Select(declaration => new AgentWorkspaceResource(CopyIdentityKinds.ConnectionReference, declaration.LogicalName))).Distinct().ToArray();
    }

    /// <summary>Returns the resources that exist in the environment the client targets, in their given order.</summary>
    /// <param name="client">The client, already targeting the environment to search.</param>
    /// <param name="resources">The flows, AI prompts, custom connectors, and connection references to look for.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The resources that exist.</returns>
    public static async Task<IReadOnlyList<AgentWorkspaceResource>> FindExistingAsync(ICopyDataverseClient client, IReadOnlyList<AgentWorkspaceResource> resources, CancellationToken cancellationToken)
    {
        if (client is null)
        {
            throw new ArgumentNullException(nameof(client));
        }

        if (resources is null)
        {
            throw new ArgumentNullException(nameof(resources));
        }

        var existingIdentifiers = new HashSet<(string Kind, Guid Identifier)>();

        foreach (var kind in resources.Where(resource => resource.Kind != CopyIdentityKinds.ConnectionReference).GroupBy(resource => resource.Kind, StringComparer.Ordinal))
        {
            existingIdentifiers.UnionWith((await FindExistingIdentifiersAsync(client, kind.Key, kind.Select(IdentifierOf).Where(identifier => identifier != Guid.Empty).Distinct().ToArray(), cancellationToken).ConfigureAwait(false)).Select(identifier => (kind.Key, identifier)));
        }

        var referenceNames = resources.Where(resource => resource.Kind == CopyIdentityKinds.ConnectionReference).Select(resource => resource.Identity).ToArray();
        var existingReferences = new HashSet<string>(referenceNames.Length == 0 ? Enumerable.Empty<string>() : (await client.GetConnectionReferencesByLogicalNamesAsync(referenceNames, cancellationToken).ConfigureAwait(false)).Select(reference => reference.ConnectionReferenceLogicalName), StringComparer.OrdinalIgnoreCase);

        return resources.Where(resource => resource.Kind == CopyIdentityKinds.ConnectionReference ? existingReferences.Contains(resource.Identity) : existingIdentifiers.Contains((resource.Kind, IdentifierOf(resource)))).ToArray();
    }

    internal static IEnumerable<WorkspacePromptFolder> PromptFolders(string root)
    {
        var promptsDirectory = Path.Combine(root, AiPromptLayout.FolderName);

        return Directory.Exists(promptsDirectory) ? Directory.EnumerateDirectories(promptsDirectory).Select(folder => ComponentFolderName.ExtractTrailingGuid(Path.GetFileName(folder)) is Guid modelId ? new WorkspacePromptFolder(folder, Path.GetFileName(folder), modelId) : null).OfType<WorkspacePromptFolder>() : Enumerable.Empty<WorkspacePromptFolder>();
    }

    internal static IEnumerable<string> ConnectorFolders(string root)
    {
        var connectorsDirectory = Path.Combine(root, CustomConnectorLayout.FolderName);

        return Directory.Exists(connectorsDirectory) ? Directory.EnumerateDirectories(connectorsDirectory) : Enumerable.Empty<string>();
    }

    internal static WorkspaceConnector? ReadConnector(string folder)
    {
        var metadataPath = Path.Combine(folder, CustomConnectorLayout.MetadataFileName);

        return File.Exists(metadataPath) && WorkspaceSynchronizer.ParseLocalConnector(WorkspaceCopyTransform.ReadText(metadataPath, out _), Path.GetFileName(folder)) is { } metadata ? new WorkspaceConnector(metadata.ConnectorId, Path.GetFileName(folder), metadata.Name, metadata.DisplayName, metadata.ConnectorInternalId, metadata.VersionNumber) : null;
    }

    internal static IEnumerable<string> DeclarationFiles(string root)
    {
        if (File.Exists(Path.Combine(root, WorkspaceCopyTransform.ClassicConnectionReferencesFileName)))
        {
            yield return WorkspaceCopyTransform.ClassicConnectionReferencesFileName;
        }

        var connections = Path.Combine(root, CliAgentConnectionsWriter.InfrastructureConnectionsFolder.Replace('/', Path.DirectorySeparatorChar));

        if (!Directory.Exists(connections))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(connections, "*" + CliAgentConnectionsWriter.FileExtension).OrderBy(path => path, StringComparer.Ordinal))
        {
            yield return CliAgentConnectionsWriter.InfrastructureConnectionsFolder + "/" + Path.GetFileName(file);
        }
    }

    internal static IReadOnlyList<WorkspaceDeclaration> ReadDeclarations(string root, string relative) => ConnectionReferenceStore.ParseFlat(WorkspaceCopyTransform.ReadText(Path.Combine(root, relative), out _)).Where(reference => !string.IsNullOrEmpty(reference.ConnectionReferenceLogicalName.Value)).Select(reference => new WorkspaceDeclaration(relative, reference.ConnectionReferenceLogicalName.Value, reference.ConnectorId.ToString())).ToArray();

    private static Guid IdentifierOf(AgentWorkspaceResource resource) => Guid.TryParse(resource.Identity, out var identifier) ? identifier : Guid.Empty;

    private static Task<IReadOnlyCollection<Guid>> FindExistingIdentifiersAsync(ICopyDataverseClient client, string kind, IReadOnlyCollection<Guid> identifiers, CancellationToken cancellationToken) => kind switch
    {
        CopyIdentityKinds.Workflow => client.FindExistingWorkflowIdsAsync(identifiers, cancellationToken),
        CopyIdentityKinds.AIModel => client.FindExistingAIModelIdsAsync(identifiers, cancellationToken),
        CopyIdentityKinds.Connector => client.FindExistingConnectorIdsAsync(identifiers, cancellationToken),
        _ => throw new ArgumentException($"An agent holds no {kind} resource.", nameof(kind)),
    };

    private static T Parsed<T>(Func<T> read, string relative)
    {
        try
        {
            return read();
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            throw new InvalidDataException($"{relative} could not be read: {failure.Message}", failure);
        }
    }
}

internal sealed record WorkspacePromptFolder(string FolderPath, string FolderName, Guid ModelId);

internal sealed record WorkspaceConnector(Guid Id, string FolderName, string? Name, string? DisplayName, string? InternalId, long? Version);

internal sealed record WorkspaceDeclaration(string File, string LogicalName, string? ConnectorId);
