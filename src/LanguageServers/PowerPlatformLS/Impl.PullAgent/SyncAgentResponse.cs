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

        /// <summary>
        /// Display names of custom connectors that were newly created in Dataverse
        /// during this push (i.e. did not exist before). Empty when none were created.
        /// </summary>
        public ImmutableArray<string> NewlyCreatedCustomConnectors { get; init; } = ImmutableArray<string>.Empty;
    }
}