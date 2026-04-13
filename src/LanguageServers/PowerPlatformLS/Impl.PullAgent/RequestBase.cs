namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.CopilotStudio.Sync;

    internal abstract class RequestBase
    {
        public required CoreServicesClusterCategory ClusterCategory { get; set; }
    }
}