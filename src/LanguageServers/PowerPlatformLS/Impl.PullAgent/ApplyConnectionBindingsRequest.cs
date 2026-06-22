namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.CopilotStudio.Sync;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using System.Collections.Immutable;

    internal class ApplyConnectionBindingsRequest : DataverseRequest, IHasWorkspace
    {
        public const string MessageName = "powerplatformls/applyConnectionBindings";

        public required Uri WorkspaceUri { get; set; }

        public string? ConnectionsAccessToken { get; set; }

        public ImmutableArray<ConnectionBindingRequest> Bindings { get; set; } = ImmutableArray<ConnectionBindingRequest>.Empty;
    }
}
