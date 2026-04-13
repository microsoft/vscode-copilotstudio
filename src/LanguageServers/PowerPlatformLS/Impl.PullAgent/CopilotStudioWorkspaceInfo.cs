namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.CopilotStudio.Sync;
    using System.Collections.Immutable;

    internal enum WorkspaceType
    {
        Unknown = -1,
        Agent = 0,
        ComponentCollection = 1,
    }

    internal class CopilotStudioWorkspaceInfo
    {
        public required Uri WorkspaceUri { get; set; }

        public string? IconFilePath { get; set; }

        public string? DisplayName { get; set; }

        public string? Description { get; set; }

        public WorkspaceType Type { get; set; }

        public AgentSyncInfo? SyncInfo { get; set; }

        public ImmutableArray<CopilotStudioWorkspaceInfo> ComponentCollections { get; set; } = ImmutableArray<CopilotStudioWorkspaceInfo>.Empty;
    }
}
