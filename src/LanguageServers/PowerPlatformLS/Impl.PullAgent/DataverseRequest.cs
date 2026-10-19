namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.Agents.Platform.Content;
    using Microsoft.CopilotStudio.Sync;

    internal abstract class DataverseRequest
    {
        public required EnvironmentInfo EnvironmentInfo { get; set; }

        public required SolutionInfo SolutionVersions { get; set; }

        public required AccountInfo AccountInfo { get; set; }

        public required string DataverseAccessToken { get; set; }

        public required string CopilotStudioAccessToken { get; set; }

        /// <summary>Builds the workspace sync binding this request describes.</summary>
        /// <param name="agentId">The agent the workspace holds, or <see langword="null"/> when it holds none.</param>
        public AgentSyncInfo BuildSyncInfo(Guid? agentId) => new AgentSyncInfo
        {
            AgentId = agentId,
            DataverseEndpoint = new Uri(EnvironmentInfo.DataverseUrl),
            EnvironmentId = EnvironmentInfo.EnvironmentId,
            EnvironmentDisplayName = EnvironmentInfo.DisplayName,
            SolutionVersions = SolutionVersions,
            AccountInfo = AccountInfo,
            AgentManagementEndpoint = new Uri(EnvironmentInfo.AgentManagementUrl),
        };
    }
}
