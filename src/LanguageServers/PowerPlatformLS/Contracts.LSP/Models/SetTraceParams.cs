namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Models
{
    /// <summary>
    /// Parameters for the $/setTrace notification.
    /// Implements IDefaultContextRequest so the RequestContextFactory
    /// does not log a warning about unresolved context.
    /// </summary>
    public sealed class SetTraceParams : IDefaultContextRequest
    {
        public string? Value { get; set; }
    }
}
