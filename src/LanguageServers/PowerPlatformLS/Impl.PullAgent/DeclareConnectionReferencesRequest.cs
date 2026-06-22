namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using System.Collections.Immutable;

    internal class DeclareConnectionReferencesRequest : DataverseRequest, IHasWorkspace
    {
        public const string MessageName = "powerplatformls/declareConnectionReferences";

        public required Uri WorkspaceUri { get; set; }

        public ImmutableArray<string> LogicalNames { get; set; } = ImmutableArray<string>.Empty;

        public string? ConnectionsAccessToken { get; set; }
    }
}
