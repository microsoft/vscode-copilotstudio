namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using System.Collections.Immutable;

    internal class ReattachAgentResponse : ResponseBase
    {
        public AgentSyncInfo? AgentSyncInfo { get; init; }

        public bool IsNewAgent { get; init; } = false;

        public ImmutableArray<WorkflowResponse> WorkflowResponse { get; init; } = ImmutableArray<WorkflowResponse>.Empty;
    }
}
