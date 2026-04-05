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
    }
}
