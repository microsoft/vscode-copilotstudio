namespace Microsoft.PowerPlatformLS.Impl.Core.LSP.Models
{
    using System.Collections.Immutable;

    internal class ListWorkspacesResponse
    {
        public ImmutableArray<Uri> WorkspaceUris { get; init; } = ImmutableArray<Uri>.Empty;
    }
}
