namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using System.Collections.Immutable;

    internal class SyncAgentRequest : DataverseRequest, IHasWorkspace
    {
        public required Uri WorkspaceUri { get; set; }

        public ImmutableArray<ConnectionBindingInput> ConnectionBindings { get; set; } = ImmutableArray<ConnectionBindingInput>.Empty;
    }
}