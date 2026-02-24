

namespace Microsoft.PowerPlatformLS.Impl.Core
{
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.CommonLanguageServerProtocol.Framework.JsonRpc;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Hosting;
    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using Microsoft.VisualStudio.Threading;

    /// <summary>
    /// Hosted service responsible for listening to incoming messages from the client and processing them.
    /// </summary>
    /// <param name="stream">The stream used to read messages sent by the client (typically vs-code extension).</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="server"></param>
    internal sealed class LanguageServerListener(IJsonRpcStream stream, ILspLogger logger, ILanguageServer server) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            logger.LogInformation("Starting Language Server Listener...");

            var streamTask = stream.RunAsync(stoppingToken);

            logger.LogInformation("Language Server Listening!");
            await streamTask;

            logger.LogInformation("Stream Ended. Waiting for Exit signal...");
            await server.WaitForExitAsync();

            logger.LogInformation("Server Exited. Listener Ends.");
        }
    }
}