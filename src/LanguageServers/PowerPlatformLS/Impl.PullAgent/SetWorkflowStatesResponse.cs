namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.CopilotStudio.Sync;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using System.Collections.Immutable;

    internal class SetWorkflowStatesResponse : ResponseBase
    {
        public bool Succeeded { get; init; }

        public ImmutableArray<WorkflowStatusView> Workflows { get; init; } = ImmutableArray<WorkflowStatusView>.Empty;
    }
}
