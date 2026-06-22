namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;

    internal class ListConnectorsRequest : DataverseRequest, IHasWorkspace
    {
        public const string MessageName = "powerplatformls/listConnectors";

        public required Uri WorkspaceUri { get; set; }

        public string? ConnectionsAccessToken { get; set; }
    }
}
