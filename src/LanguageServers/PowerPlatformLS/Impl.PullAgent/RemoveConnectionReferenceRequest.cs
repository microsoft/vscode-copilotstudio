namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;

    internal class RemoveConnectionReferenceRequest : DataverseRequest, IHasWorkspace
    {
        public const string MessageName = "powerplatformls/removeConnectionReference";

        public required Uri WorkspaceUri { get; set; }

        public required string LogicalName { get; set; }

        public bool Confirmed { get; set; }
    }
}
