namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;

    internal class PreparePushRequest : DataverseRequest, IHasWorkspace
    {
        public const string MessageName = "powerplatformls/preparePush";

        public required Uri WorkspaceUri { get; set; }
    }
}
