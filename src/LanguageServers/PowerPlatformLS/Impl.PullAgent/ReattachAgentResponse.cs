namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.CopilotStudio.Sync;
    using Microsoft.CopilotStudio.Sync.Dataverse;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using System.Collections.Immutable;

    internal class ReattachAgentResponse : ResponseBase
    {
        public AgentSyncInfo? AgentSyncInfo { get; init; }

        public bool IsNewAgent { get; init; } = false;

        public ImmutableArray<WorkflowResponse> WorkflowResponse { get; init; } = ImmutableArray<WorkflowResponse>.Empty;

        public ImmutableArray<SyncDataverseClient.AIPromptResponse> AIPromptResponse { get; init; } = ImmutableArray<SyncDataverseClient.AIPromptResponse>.Empty;
    }
}
