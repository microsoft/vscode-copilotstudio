namespace Microsoft.PowerPlatformLS.Contracts.Internal.Common
{
    using System.Threading;

    /// <summary>
    /// Provides ambient context for the current LSP request ID.
    /// Set by JsonRpcStream when a custom method is received, read by any
    /// downstream service (HTTP handler, operation logger) to correlate logs.
    /// </summary>
    public static class LspRequestContext
    {
        private static readonly AsyncLocal<int> _currentRequestId = new();

        /// <summary>
        /// Gets or sets the current LSP request ID for the executing async flow.
        /// Returns 0 when no request is active.
        /// </summary>
        public static int CurrentRequestId
        {
            get => _currentRequestId.Value;
            set => _currentRequestId.Value = value;
        }
    }
}
