namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.CopilotStudio.Sync;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using System.Collections.Immutable;

    internal class PreparePushResponse : ResponseBase
    {
        public ImmutableArray<ConnectionNeeded> AgentConnections { get; init; } = ImmutableArray<ConnectionNeeded>.Empty;
    }
}
