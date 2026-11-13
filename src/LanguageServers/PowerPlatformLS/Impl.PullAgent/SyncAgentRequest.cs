namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using System.Collections.Immutable;

    internal class SyncAgentRequest : DataverseRequest, IHasWorkspace
    {
        public required Uri WorkspaceUri { get; set; }

        public bool DraftConnectionReferenceWorkflows { get; set; }

        public bool OverwriteLocalChanges { get; set; }

        public bool RequireNoUnpulledChanges { get; set; }

        public ImmutableArray<string>? ApprovedEnvironmentVariableOverwrites { get; set; }
    }
}