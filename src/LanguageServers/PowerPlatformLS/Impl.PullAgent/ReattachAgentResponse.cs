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

        /// <summary>
        /// Display names of custom connectors that were newly created in Dataverse during reattach.
        /// Empty when no new connectors were created.
        /// </summary>
        public ImmutableArray<string> NewlyCreatedCustomConnectors { get; init; } = ImmutableArray<string>.Empty;
    }
}
