namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using System.Collections.Generic;

    internal class SetWorkflowStatesRequest : DataverseRequest, IHasWorkspace
    {
        public const string MessageName = "powerplatformls/setWorkflowStates";

        public required Uri WorkspaceUri { get; set; }

        public IReadOnlyList<WorkflowStateChange> Changes { get; set; } = new List<WorkflowStateChange>();

        public string? ConnectionsAccessToken { get; set; }
    }

    internal class WorkflowStateChange
    {
        public string WorkflowId { get; set; } = string.Empty;

        public bool Activate { get; set; }
    }
}
