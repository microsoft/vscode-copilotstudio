namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.Agents.Platform.Content;
    using System.Collections.Immutable;
    using System.Text.Json.Serialization;

    internal enum WorkspaceType
    {
        Unknown = -1,
        Agent = 0,
        ComponentCollection = 1,
    }

    internal class CopilotStudioWorkspaceInfo
    {
        public required Uri WorkspaceUri { get; set; }
        public required WorkspaceType Type { get; set; }
        public required AgentSyncInfo? SyncInfo { get; set; }
        public required string DisplayName { get; set; }
        public required string? Description { get; set; }
        public required string? IconFilePath { get; set; }
    }

    /// <summary>
    /// Information to connect to the cloud service. This can be used to upload/download the agents.
    /// This gets saved as ".mcs/conn.json" and used for sync operations. 
    /// </summary>
    internal class AgentSyncInfo
    {        
        /// <summary>
        /// Url to dataverse org. Like "https://org12345678.crm.dynamics.com/"
        /// </summary>
        public required Uri DataverseEndpoint { get; init; }

        /// <summary>
        /// Environment Id often looks like a guid, but it can be an arbitrary strin
        /// </summary>
        public required string EnvironmentId { get; init; }

        public required AccountInfo? AccountInfo { get; init; }

        // Only one of these Ids is set depending on workspace type. . 
        public Guid? AgentId { get; init; }

        public Guid? ComponentCollectionId { get; init; }

        public required SolutionInfo? SolutionVersions { get; init; }

        [JsonIgnore]
        public BotReference BotReference => (AgentId == null) ? throw new InvalidOperationException($"No AgentId") : new(EnvironmentId, AgentId.Value);

        /// <summary>
        /// Url to copilot studio control plane, Like "https://powervamg.us-il301.gateway.prod.island.powerapps.com/"
        /// </summary>
        public required Uri AgentManagementEndpoint { get; init; }
    }

    internal class AssetsToClone
    {
        public ImmutableArray<Guid> ComponentCollectionIds { get; set; } = ImmutableArray<Guid>.Empty;

        public required bool CloneAgent { get; set; }
    }
}
