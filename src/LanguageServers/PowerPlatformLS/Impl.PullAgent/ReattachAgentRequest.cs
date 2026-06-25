namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;

    internal enum RetargetConflictResolution
    {
        Prompt = 0,

        ReuseExisting = 1,
    }

    internal class ReattachAgentRequest : DataverseRequest, IHasWorkspace
    {
        public const string MessageName = "powerplatformls/reattachAgent";

        public required Uri WorkspaceUri { get; set; }

        public bool AllowRetarget { get; set; }

        public RetargetConflictResolution ConflictResolution { get; set; }
    }
}
