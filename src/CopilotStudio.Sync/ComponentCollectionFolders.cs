// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Collections.Immutable;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.CopilotStudio.Sync;

/// <summary>The component-collection folders an agent workspace declares, split into the ones that hold a collection project inside the project and the declarations that do not.</summary>
public sealed class ComponentCollectionFolderResolution
{
    /// <summary>The declared folders that hold a collection project inside the project, in declaration order without repeats.</summary>
    public ImmutableArray<DirectoryPath> Found { get; init; } = ImmutableArray<DirectoryPath>.Empty;

    /// <summary>The declared directories, as written, that resolve to no collection project inside the project.</summary>
    public ImmutableArray<string> Missing { get; init; } = ImmutableArray<string>.Empty;
}

/// <summary>A component collection and the other agents it is linked to in an environment.</summary>
internal sealed class ComponentCollectionLinks
{
    /// <summary>The folder holding the collection project.</summary>
    public DirectoryPath Folder { get; init; }

    /// <summary>The collection's schema name.</summary>
    public string SchemaName { get; init; } = string.Empty;

    /// <summary>The agents other than the one being pushed that the collection is linked to.</summary>
    public ImmutableArray<AgentInfo> OtherAgents { get; init; } = ImmutableArray<AgentInfo>.Empty;
}

/// <summary>Why a component-collection folder cannot be told apart by the schema name its collection file declares.</summary>
public enum ComponentCollectionFolderProblemKind
{
    /// <summary>The folder holds no collection file, or one that cannot be read or names no schema name.</summary>
    SchemaNameMissing = 0,

    /// <summary>The folder declares the schema name an earlier folder declares, ignoring case.</summary>
    SchemaNameRepeated = 1,
}

/// <summary>A component-collection folder refused because its schema name cannot identify it.</summary>
/// <param name="Kind">Why the folder is refused.</param>
/// <param name="Folder">The folder, as the caller names it.</param>
/// <param name="SchemaName">The schema name the folder declares, or <see langword="null"/> when it declares none.</param>
/// <param name="FirstFolder">The earlier folder that declares the same schema name, or <see langword="null"/> when the folder repeats none.</param>
public sealed record ComponentCollectionFolderProblem(ComponentCollectionFolderProblemKind Kind, string Folder, string? SchemaName, string? FirstFolder);

/// <summary>The schema names a set of component-collection folders declare, and the folders refused because their schema name cannot identify them.</summary>
/// <param name="Folders">The folder of each declared schema name, compared ignoring case, in the order the folders were read.</param>
/// <param name="Problems">The refused folders, in the order they were read.</param>
public sealed record ComponentCollectionSchemaNames(IReadOnlyDictionary<string, string> Folders, IReadOnlyList<ComponentCollectionFolderProblem> Problems);

/// <summary>Resolves the component-collection folders an agent declares in its references file, and reports which other agents share them.</summary>
public static class ComponentCollectionFolders
{

    /// <summary>Resolves the collections an agent workspace on disk declares, refusing any whose folder the caller's physical-path resolver places outside the project.</summary>
    /// <param name="projectDirectory">The project directory every collection has to stay beneath.</param>
    /// <param name="agentWorkspaceDirectory">The directory holding the agent's workspace.</param>
    /// <param name="resolvePhysicalPath">Resolves a path on disk through any symbolic link or junction on the way to it.</param>
    public static ComponentCollectionFolderResolution Resolve(string projectDirectory, string agentWorkspaceDirectory, Func<string, string> resolvePhysicalPath)
    {
        if (projectDirectory is null)
        {
            throw new ArgumentNullException(nameof(projectDirectory));
        }

        if (agentWorkspaceDirectory is null)
        {
            throw new ArgumentNullException(nameof(agentWorkspaceDirectory));
        }

        if (resolvePhysicalPath is null)
        {
            throw new ArgumentNullException(nameof(resolvePhysicalPath));
        }

        var physicalRoot = FileAccessorFactory.ToDirectoryPath(resolvePhysicalPath(projectDirectory));

        return Resolve(new FileAccessorFactory(), FileAccessorFactory.ToDirectoryPath(Path.GetFullPath(agentWorkspaceDirectory)), folder => physicalRoot.Contains(FileAccessorFactory.ToDirectoryPath(resolvePhysicalPath(folder.ToString()))));
    }

    /// <summary>Resolves the collections an agent workspace declares through the accessors a factory opens, keeping the ones whose folder lies inside the project root.</summary>
    /// <param name="workspaces">Opens the agent and collection folders.</param>
    /// <param name="projectRoot">The project root every collection has to stay beneath.</param>
    /// <param name="agentWorkspaceFolder">The folder holding the agent's workspace.</param>
    public static ComponentCollectionFolderResolution Resolve(IFileAccessorFactory workspaces, DirectoryPath projectRoot, DirectoryPath agentWorkspaceFolder)
    {
        if (workspaces is null)
        {
            throw new ArgumentNullException(nameof(workspaces));
        }

        return Resolve(workspaces, agentWorkspaceFolder, projectRoot.Contains);
    }


    /// <summary>Reads the schema name each component-collection folder's collection file declares, keeping the first folder of each name and refusing a folder that declares none or repeats an earlier folder's name, ignoring case.</summary>
    /// <param name="collectionFolders">Each folder, as the caller names it, with the workspace it holds, in the order to read them.</param>
    public static ComponentCollectionSchemaNames ReadSchemaNames(IEnumerable<KeyValuePair<string, IFileAccessor>> collectionFolders)
    {
        if (collectionFolders is null)
        {
            throw new ArgumentNullException(nameof(collectionFolders));
        }

        var folders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var problems = new List<ComponentCollectionFolderProblem>();

        foreach (var collection in collectionFolders)
        {
            var schemaName = ReadSchemaNameOrNull(collection.Value ?? throw new ArgumentException($"The collection folder '{collection.Key}' has no workspace.", nameof(collectionFolders)));

            if (schemaName is null)
            {
                problems.Add(new ComponentCollectionFolderProblem(ComponentCollectionFolderProblemKind.SchemaNameMissing, collection.Key, null, null));
            }
            else if (folders.TryGetValue(schemaName, out var firstFolder))
            {
                problems.Add(new ComponentCollectionFolderProblem(ComponentCollectionFolderProblemKind.SchemaNameRepeated, collection.Key, schemaName, firstFolder));
            }
            else
            {
                folders.Add(schemaName, collection.Key);
            }
        }

        return new ComponentCollectionSchemaNames(folders, problems);
    }

    /// <summary>Reads the schema name each component-collection directory on disk declares, naming each folder by its directory as given and refusing one that declares none or repeats an earlier directory's name, ignoring case.</summary>
    /// <param name="collectionDirectories">The collection directories on disk, in the order to read them.</param>
    public static ComponentCollectionSchemaNames ReadSchemaNames(IEnumerable<string> collectionDirectories) => ReadSchemaNames((collectionDirectories ?? throw new ArgumentNullException(nameof(collectionDirectories))).Select(directory => new KeyValuePair<string, IFileAccessor>(directory ?? throw new ArgumentException("A collection directory is null.", nameof(collectionDirectories)), FileAccessorFactory.OpenFolder(directory))));

    internal static string Describe(ComponentCollectionFolderProblem problem) => problem.Kind == ComponentCollectionFolderProblemKind.SchemaNameRepeated ? $"The declared component collection folders '{problem.FirstFolder}' and '{problem.Folder}' both declare the schema name '{problem.SchemaName}'." : $"The declared component collection folder '{problem.Folder}' holds a {WorkspaceSynchronizer.ComponentCollectionPath} that cannot be read or names no schema name.";

    /// <summary>Reads, in one batched query per fifty collections, the agents other than the pushed one that each collection is linked to.</summary>
    /// <param name="workspaces">Opens the collection folders to read their schema names.</param>
    /// <param name="collectionFolders">The folders of the collections that were pushed.</param>
    /// <param name="agentId">The agent the collections were pushed for, left out of each collection's other agents.</param>
    /// <param name="dataverseClient">The client, already targeting the environment the collections were pushed to.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    internal static async Task<ImmutableArray<ComponentCollectionLinks>> GetOtherLinkedAgentsAsync(IFileAccessorFactory workspaces, IReadOnlyList<DirectoryPath> collectionFolders, Guid agentId, ISyncDataverseClient dataverseClient, CancellationToken cancellationToken)
    {
        if (workspaces is null)
        {
            throw new ArgumentNullException(nameof(workspaces));
        }

        if (collectionFolders is null)
        {
            throw new ArgumentNullException(nameof(collectionFolders));
        }

        var linkClient = LinkClientOf(dataverseClient);

        var collections = collectionFolders.Select(folder => WorkspaceSynchronizer.ReadComponentCollectionSchemaName(workspaces.Create(folder)) is { } schemaName ? new ComponentCollectionLinks { Folder = folder, SchemaName = schemaName } : null).OfType<ComponentCollectionLinks>().ToArray();
        if (collections.Length == 0)
        {
            return ImmutableArray<ComponentCollectionLinks>.Empty;
        }

        var linked = await linkClient.GetLinkedAgentsByComponentCollectionSchemaNamesAsync(collections.Select(collection => collection.SchemaName), cancellationToken).ConfigureAwait(false);

        return collections.Select(collection => new ComponentCollectionLinks
        {
            Folder = collection.Folder,
            SchemaName = collection.SchemaName,
            OtherAgents = linked.TryGetValue(collection.SchemaName, out var agents) ? agents.Where(agent => agent.AgentId != agentId).ToImmutableArray() : ImmutableArray<AgentInfo>.Empty,
        }).ToImmutableArray();
    }

    internal static ISyncComponentCollectionLinkDataverseClient LinkClientOf(ISyncDataverseClient dataverseClient) => dataverseClient as ISyncComponentCollectionLinkDataverseClient ?? throw new InvalidOperationException("Dataverse client does not support component collection link queries.");

    private static ComponentCollectionFolderResolution Resolve(IFileAccessorFactory workspaces, DirectoryPath agentWorkspaceFolder, Func<DirectoryPath, bool> isInsideProject)
    {
        var references = WorkspaceSynchronizer.ReadReferencesOrNull(workspaces.Create(agentWorkspaceFolder));
        if (references is null)
        {
            return new ComponentCollectionFolderResolution();
        }

        var located = references.ComponentCollections.Select(item => item.Directory?.Trim()).Where(directory => !string.IsNullOrEmpty(directory)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Select(directory => (Declared: directory, Folder: agentWorkspaceFolder.ResolveRelativeRef(new RelativeDirectoryPath(directory.Replace('\\', '/'))))).Select(candidate => (candidate.Declared, Folder: isInsideProject(candidate.Folder) && workspaces.Create(candidate.Folder).Exists(WorkspaceSynchronizer.ComponentCollectionPath) ? candidate.Folder : (DirectoryPath?)null)).ToArray();

        return new ComponentCollectionFolderResolution
        {
            Found = located.Select(candidate => candidate.Folder).OfType<DirectoryPath>().Distinct().ToImmutableArray(),
            Missing = located.Where(candidate => !candidate.Folder.HasValue).Select(candidate => candidate.Declared).ToImmutableArray(),
        };
    }

    private static string? ReadSchemaNameOrNull(IFileAccessor collectionWorkspace)
    {
        try
        {
            return WorkspaceSynchronizer.ReadComponentCollectionSchemaName(collectionWorkspace);
        }
        catch (Exception failure) when (WorkspaceSynchronizer.IsProjectionSerializationFailure(failure) || failure is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
