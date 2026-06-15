namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.CopilotStudio.Sync;
    using Microsoft.CopilotStudio.Sync.Dataverse;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using System.Collections.Immutable;

    internal class SyncAgentResponse : ResponseBase
    {
        public ImmutableArray<Change> LocalChanges { get; set; } = ImmutableArray<Change>.Empty;

        public ImmutableArray<WorkflowResponse> WorkflowResponse { get; init; } = ImmutableArray<WorkflowResponse>.Empty;

        public ImmutableArray<SyncDataverseClient.AIPromptResponse> AIPromptResponse { get; init; } = ImmutableArray<SyncDataverseClient.AIPromptResponse>.Empty;
    }
}