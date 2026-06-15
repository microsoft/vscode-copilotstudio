namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.CopilotStudio.Sync;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using System.Collections.Immutable;

    /// <summary>
    /// ReattachAgentRequest is used to reattach the agent to a workspace.
    /// </summary>
    internal class ReattachAgentRequest : DataverseRequest, IHasWorkspace
    {
        public const string MessageName = "powerplatformls/reattachAgent";

        public required Uri WorkspaceUri { get; set; }

        public AgentSyncInfo? AgentSyncInfo { get; set; }

        public ImmutableArray<ConnectionBindingInput> ConnectionBindings { get; set; } = ImmutableArray<ConnectionBindingInput>.Empty;

        public bool IsNewAgent { get; set; } = false;

        public bool UpdateWorkspaceDirectory { get; set; } = false;
    }

    /// <summary>
    /// A connection reference logical name paired with the logical name of the connection to bind it to.
    /// </summary>
    internal class ConnectionBindingInput
    {
        public string ConnectionReferenceLogicalName { get; set; } = string.Empty;

        public string ConnectionLogicalName { get; set; } = string.Empty;

        public string? ConnectionDisplayName { get; set; }
    }
}
