namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.CopilotStudio.Sync;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using System.Collections.Immutable;

    internal class ListWorkflowStatusResponse : ResponseBase
    {
        public ImmutableArray<WorkflowStatusView> Workflows { get; init; } = ImmutableArray<WorkflowStatusView>.Empty;
    }
}
