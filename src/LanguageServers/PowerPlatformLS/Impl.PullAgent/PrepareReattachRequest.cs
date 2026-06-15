namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;

    internal class PrepareReattachRequest : DataverseRequest, IHasWorkspace
    {
        public const string MessageName = "powerplatformls/prepareReattach";

        public required Uri WorkspaceUri { get; set; }
    }
}
