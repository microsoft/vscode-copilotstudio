namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;

    internal class CreateConnectionReferenceResponse : ResponseBase
    {
        public string LogicalName { get; init; } = string.Empty;
    }
}
