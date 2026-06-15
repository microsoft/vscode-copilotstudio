namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.CopilotStudio.Sync;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using System.Collections.Immutable;

    internal class PrepareReattachResponse : ResponseBase
    {
        public AgentSyncInfo? AgentSyncInfo { get; init; }

        public bool IsNewAgent { get; init; } = false;

        public bool UpdateWorkspaceDirectory { get; init; } = false;

        public ImmutableArray<ConnectionNeeded> AgentConnections { get; init; } = ImmutableArray<ConnectionNeeded>.Empty;
    }
}
