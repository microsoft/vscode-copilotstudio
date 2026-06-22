namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;

    internal class ListWorkflowStatusRequest : DataverseRequest, IHasWorkspace
    {
        public const string MessageName = "powerplatformls/listWorkflowStatus";

        public required Uri WorkspaceUri { get; set; }

        public string? ConnectionsAccessToken { get; set; }
    }
}
