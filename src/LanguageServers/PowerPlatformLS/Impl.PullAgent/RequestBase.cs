namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    internal abstract class RequestBase
    {
        public required CoreServicesClusterCategory ClusterCategory { get; set; }
    }
}