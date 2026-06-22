namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;

    internal class CreateConnectionReferenceRequest : DataverseRequest, IHasWorkspace
    {
        public const string MessageName = "powerplatformls/createConnectionReference";

        public required Uri WorkspaceUri { get; set; }

        public required string ConnectorInternalId { get; set; }

        public string? ConnectionsAccessToken { get; set; }
    }
}
