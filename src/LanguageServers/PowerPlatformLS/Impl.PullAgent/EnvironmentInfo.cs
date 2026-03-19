namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    internal class EnvironmentInfo
    {
        public required string EnvironmentId { get; set; }

        public required string DataverseUrl { get; set; }

        public required string DisplayName { get; set; }

        public required string AgentManagementUrl { get; set; }
    }
}
