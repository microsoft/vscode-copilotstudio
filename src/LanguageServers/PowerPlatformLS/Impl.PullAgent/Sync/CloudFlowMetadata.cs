namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.Agents.ObjectModel;
    using System.Collections.Immutable;

    internal class CloudFlowMetadata
    {
        public ImmutableArray<CloudFlowDefinition> Workflows { get; init; } = ImmutableArray<CloudFlowDefinition>.Empty;

        public ImmutableArray<ConnectionReference> ConnectionReferences { get; init; } = ImmutableArray<ConnectionReference>.Empty;
    }
}
