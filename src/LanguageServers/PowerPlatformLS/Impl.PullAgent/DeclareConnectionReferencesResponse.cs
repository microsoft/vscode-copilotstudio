namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.CopilotStudio.Sync;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using System.Collections.Immutable;

    internal class DeclareConnectionReferencesResponse : ResponseBase
    {
        public ImmutableArray<AgentConnectionView> AgentConnections { get; init; } = ImmutableArray<AgentConnectionView>.Empty;

        public ImmutableArray<string> InvalidLogicalNames { get; init; } = ImmutableArray<string>.Empty;
    }
}
