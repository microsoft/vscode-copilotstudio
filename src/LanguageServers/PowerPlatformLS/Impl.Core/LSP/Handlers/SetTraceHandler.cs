namespace Microsoft.PowerPlatformLS.Impl.Core.Lsp.Handlers
{
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Handles the $/setTrace notification sent by VS Code when the user changes the log level.
    /// This is a standard LSP notification that servers should accept gracefully.
    /// Our tracing is controlled via MEL log levels, so this is a no-op.
    /// </summary>
    [LanguageServerEndpoint("$/setTrace", LanguageServerConstants.DefaultLanguageName)]
    internal class SetTraceHandler : INotificationHandler<SetTraceParams, RequestContext>
    {
        public bool MutatesSolutionState => false;

        public Task HandleNotificationAsync(SetTraceParams request, RequestContext requestContext, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
