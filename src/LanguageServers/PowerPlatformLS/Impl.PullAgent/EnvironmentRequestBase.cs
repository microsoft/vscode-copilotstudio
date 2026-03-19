namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    internal abstract class EnvironmentRequestBase : RequestBase
    {
        public required string EnvironmentId { get; set; }
    }
}