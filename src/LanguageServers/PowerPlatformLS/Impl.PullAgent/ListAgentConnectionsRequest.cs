namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;

    internal class ListAgentConnectionsRequest : DataverseRequest, IHasWorkspace
    {
        public const string MessageName = "powerplatformls/listAgentConnections";

        public required Uri WorkspaceUri { get; set; }

        public string? ConnectionsAccessToken { get; set; }
    }
}
