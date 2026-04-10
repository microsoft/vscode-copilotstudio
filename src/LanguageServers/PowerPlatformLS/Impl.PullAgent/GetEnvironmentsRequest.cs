namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.CopilotStudio.Sync;

    internal class GetEnvironmentsRequest
    {
        public const string MessageName = "powerplatformls/getEnvironment";

        public required CoreServicesClusterCategory ClusterCategory { get; set; }

        public required string EnvironmentId { get; set; }
    }
}
