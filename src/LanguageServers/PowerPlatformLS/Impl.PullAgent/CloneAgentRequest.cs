namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.CopilotStudio.Sync;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using System.Text.Json.Serialization;

    /// <summary>
    /// The incoming request over the LSP for clone an agent.
    /// Assume this input is untrusted and must be validated.
    /// </summary>
    internal class CloneAgentRequest : DataverseRequest, IDefaultContextRequest
    {
        public const string MessageName = "powerplatformls/cloneAgent";

        public required AgentInfo AgentInfo { get; set; }

        public required AssetsToClone Assets { get; set; }

        public required Uri RootFolder { get; set; }

        public AgentSyncInfo GetSyncInfo() => new AgentSyncInfo
        {
            AgentId = AgentInfo.AgentId,
            DataverseEndpoint = new Uri(EnvironmentInfo.DataverseUrl),
            EnvironmentId = EnvironmentInfo.EnvironmentId,
            SolutionVersions = SolutionVersions,
            AccountInfo = AccountInfo,
            AgentManagementEndpoint = new Uri(EnvironmentInfo.AgentManagementUrl),
        };
    }

}
