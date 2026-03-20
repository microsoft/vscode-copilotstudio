namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    internal class WorkflowResponse
    {
        public string WorkflowName { get; init; } = string.Empty;

        public bool IsDisabled { get; init; } = false;

        public string ErrorMessage { get; init; } = string.Empty;
    }
}
