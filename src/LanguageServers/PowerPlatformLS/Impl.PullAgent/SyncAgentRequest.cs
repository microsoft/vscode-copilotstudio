namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;

    internal class SyncAgentRequest : DataverseRequest, IHasWorkspace
    {
        public required Uri WorkspaceUri { get; set; }
    }
}