namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.CopilotStudio.Sync;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using System.Collections.Immutable;

    internal class ListConnectorsResponse : ResponseBase
    {
        public ImmutableArray<ConnectorInfo> Connectors { get; init; } = ImmutableArray<ConnectorInfo>.Empty;
    }
}
