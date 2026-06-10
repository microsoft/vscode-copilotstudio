namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;

    internal class CloneAgentResponse : ResponseBase
    {
        // Canonical folder name containing the top-level agent.mcs.yml (main agent clone).
        public string? AgentFolderName { get; set; }

        public AuthoringShape? AuthoringShape { get; set; }
    }
}
