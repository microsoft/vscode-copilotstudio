// Copyright (C) Microsoft Corporation. All rights reserved.
// Ported from om/src/vscode/LanguageServers/PowerPlatformLS/Impl.PullAgent/

using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.Platform.Content;
using System.Collections.Immutable;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;

namespace Microsoft.CopilotStudio.Sync;


#region Change

public record Change
{
    public string Name { get; set; } = string.Empty;

#pragma warning disable CA1056 // Uri is used as a file path string, not a System.Uri
    public string Uri { get; set; } = string.Empty;
#pragma warning restore CA1056

    public ChangeType ChangeType { get; set; }

    public string ChangeKind { get; set; } = string.Empty;

    public string SchemaName { get; set; } = string.Empty;

    public string? RemoteWorkflowContent { get; set; }
}

public enum ChangeType
{
    Create = 0,
    Update = 1,
    Delete = 2
}

#endregion

#region AgentSyncInfo

internal enum WorkspaceType
{
    Unknown = -1,
    Agent = 0,
    ComponentCollection = 1,
}

/// <summary>
/// Information to connect to the cloud service. This can be used to upload/download the agents.
/// This gets saved as ".mcs/conn.json" and used for sync operations.
/// </summary>
public class AgentSyncInfo
{
    /// <summary>
    /// Url to dataverse org. Like "https://org12345678.crm.dynamics.com/"
    /// </summary>
    public Uri DataverseEndpoint { get; init; } = null!;

    /// <summary>
    /// Environment Id often looks like a guid, but it can be an arbitrary string.
    /// </summary>
    public string EnvironmentId { get; init; } = string.Empty;

    public AccountInfo? AccountInfo { get; init; }

    // Only one of these Ids is set depending on workspace type.
    public Guid? AgentId { get; init; }

    public Guid? ComponentCollectionId { get; init; }

    public SolutionInfo? SolutionVersions { get; init; }

    [JsonIgnore]
    public BotReference BotReference => (AgentId == null) ? throw new InvalidOperationException($"No AgentId") : new(EnvironmentId, AgentId.Value);

    /// <summary>
    /// Url to copilot studio control plane. Like "https://powervamg.us-il301.gateway.prod.island.powerapps.com/"
    /// Nullable: only needed when Island cross-validation is enabled (isIslandPreauthorized = true).
    /// Each host derives this from its own discovery mechanism (BAP for pac, VS Code sessions for extension).
    /// </summary>
    public Uri? AgentManagementEndpoint { get; init; }
}

public class AssetsToClone
{
    public ImmutableArray<Guid> ComponentCollectionIds { get; set; } = ImmutableArray<Guid>.Empty;

    public bool CloneAgent { get; set; }
}

#endregion

#region AccountInfo

public class AccountInfo
{
    public string AccountId { get; set; } = string.Empty;

    public Guid TenantId { get; set; }

    public string? AccountEmail { get; set; }

    [JsonPropertyName("clusterCategory")]
    public CoreServicesClusterCategory? ClusterCategoryInternalStorage { get; set; }

    [JsonIgnore]
    public CoreServicesClusterCategory ClusterCategory
    {
        get => ClusterCategoryInternalStorage ?? CoreServicesClusterCategory.Prod;
        set => ClusterCategoryInternalStorage = value == CoreServicesClusterCategory.Prod ? null : value;
    }
}

// https://msazure.visualstudio.com/One/_git/CoreFramework?path=/src/CoreFramework/CoreFramework.CapCoreServices.TopologyModel/ClusterCategory.cs
public enum CoreServicesClusterCategory
{
    [EnumMember(Value = "Exp")]
    Exp = 0,

    [EnumMember(Value = "Dev")]
    Dev = 1,

    [EnumMember(Value = "Test")]
    Test = 2,

    [EnumMember(Value = "Preprod")]
    Preprod = 3,

    [EnumMember(Value = "FirstRelease")]
    FirstRelease = 4,

    [EnumMember(Value = "Prod")]
    Prod = 5,

    [EnumMember(Value = "Gov")]
    Gov = 6,

    [EnumMember(Value = "High")]
    High = 7,

    [EnumMember(Value = "DoD")]
    DoD = 8,

    [EnumMember(Value = "Mooncake")]
    Mooncake = 9,

    [EnumMember(Value = "Ex")]
    Ex = 10,

    [EnumMember(Value = "Rx")]
    Rx = 11,

    [EnumMember(Value = "Prv")]
    Prv = 12,

    [EnumMember(Value = "Local")]
    Local = 13,

    [EnumMember(Value = "GovFR")]
    GovFR = 14,
}

#endregion

#region AgentInfo / SolutionInfo

public class AgentInfo
{
    public Guid AgentId { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    public string? IconBase64 { get; set; }

#pragma warning disable CA1002, CA2227 // DTO property for JSON serialization
    public List<ComponentCollectionInfo> ComponentCollections { get; set; } = new();
#pragma warning restore CA1002, CA2227

    public string? SchemaName { get; set; }
}

public class ComponentCollectionInfo
{
    public Guid Id { get; set; }

    public string SchemaName { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;
}

public class SolutionInfo
{
#pragma warning disable CA2227 // SolutionVersions must have a setter — System.Text.Json silently skips getter-only properties during deserialization, leaving the dictionary empty
    public Dictionary<string, Version> SolutionVersions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
#pragma warning restore CA2227

    public Version CopilotStudioSolutionVersion { get; set; } = null!;

    public Version GetDataverseTableSearchSolutionUniqueName() => GetSolutionVersionByName("msft_AIPlatformExtensionsComponents");

    public Version GetRelevanceSearchSolutionVersion() => GetSolutionVersionByName("msdyn_RelevanceSearch");

    private Version GetSolutionVersionByName(string name)
    {
        if (SolutionVersions.TryGetValue(name, out var version))
        {
            return version;
        }

        throw new InvalidOperationException($"Missing solution version for {name}");
    }
}

#endregion

#region CloudFlowMetadata

public class CloudFlowMetadata
{
    public ImmutableArray<CloudFlowDefinition> Workflows { get; init; } = ImmutableArray<CloudFlowDefinition>.Empty;

    public ImmutableArray<ConnectionReference> ConnectionReferences { get; init; } = ImmutableArray<ConnectionReference>.Empty;
}

#endregion

#region WorkspaceSyncInfo

public class WorkspaceSyncInfo
{
    public DefinitionBase Definition { get; init; } = null!;

    public PvaComponentChangeSet Changeset { get; init; } = null!;
}

#endregion

#region WorkflowResponse

public class WorkflowResponse
{
    public string WorkflowName { get; init; } = string.Empty;

    public bool IsDisabled { get; init; } = false;

    public string ErrorMessage { get; init; } = string.Empty;
}

#endregion

#region IMcsWorkspace

internal interface IMcsWorkspace
{
    DirectoryPath FolderPath { get; }

    DefinitionBase Definition { get; }
}

#endregion

#region PushVerification

/// <summary>
/// Result of a push verification operation.
/// Compares the workspace state against the server's current state (via re-clone)
/// to detect silent push rejection.
/// </summary>
public record PushVerificationResult
{
    /// <summary>
    /// True if all entity types in the push were accepted by the server.
    /// </summary>
    public bool IsFullyAccepted { get; init; }

    /// <summary>
    /// Per-entity-type verification details. Each entry reports whether the server's
    /// state matches the pushed state for that entity type.
    /// </summary>
    public ImmutableArray<EntityTypeVerification> EntityTypes { get; init; } = ImmutableArray<EntityTypeVerification>.Empty;
}

/// <summary>
/// Verification result for a single entity type (e.g., "topic", "dialog", "entity").
/// </summary>
public record EntityTypeVerification
{
    /// <summary>
    /// The entity/change kind (e.g., "topic", "dialog", "entity", "workflow").
    /// </summary>
    public string ChangeKind { get; init; } = string.Empty;

    /// <summary>
    /// Number of components of this type that were pushed.
    /// </summary>
    public int PushedCount { get; init; }

    /// <summary>
    /// Number of components of this type found in the server re-clone that match pushed state.
    /// </summary>
    public int VerifiedCount { get; init; }

    /// <summary>
    /// True if all pushed components of this type were accepted.
    /// </summary>
    public bool Accepted => PushedCount == VerifiedCount;
}

#endregion
