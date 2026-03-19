namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;

    /// <summary>
    /// ReattachAgentRequest is used to reattach the agent to a workspace.
    /// </summary>
    internal class ReattachAgentRequest : DataverseRequest, IHasWorkspace
    {
        public const string MessageName = "powerplatformls/reattachAgent";

        public required Uri WorkspaceUri { get; set; }
    }
}
