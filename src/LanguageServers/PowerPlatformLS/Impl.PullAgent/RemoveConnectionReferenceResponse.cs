namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.CopilotStudio.Sync;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using System.Collections.Immutable;

    internal class RemoveConnectionReferenceResponse : ResponseBase
    {
        public bool Removed { get; init; }

        public ImmutableArray<ConnectionReferenceUsage> Usages { get; init; } = ImmutableArray<ConnectionReferenceUsage>.Empty;
    }
}
