namespace Microsoft.PowerPlatformLS.Impl.Core.Lsp
{
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using System.Threading.Tasks;

    internal class LspLifeCycleManager : ILifeCycleManager
    {
        private readonly ILspLogger _logger;

        public LspLifeCycleManager(ILspLogger logger)
        {
            _logger = logger;
        }

        public Task ExitAsync()
        {
            return Task.CompletedTask;
        }

        public Task ShutdownAsync(string message = "Shutting down")
        {
            _logger.LogInformation($"{nameof(LspLifeCycleManager)}.{nameof(ShutdownAsync)} : {message}");
            return Task.CompletedTask;
        }
    }
}