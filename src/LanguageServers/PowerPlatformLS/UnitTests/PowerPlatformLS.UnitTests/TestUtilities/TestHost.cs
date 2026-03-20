namespace Microsoft.PowerPlatformLS.UnitTests.TestUtilities
{
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.DependencyInjection.Extensions;
    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging;
    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common.DependencyInjection;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Impl.Core.DependencyInjection;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.DependencyInjection;
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// TODO - Optimize: Create a static servicecollection that basic tests can share.
    /// </summary>
    internal class TestHost : IAsyncDisposable
    {
        public ITestStream TestStream { get; }
        public TestLogger Logs { get; } = new();
        public IList<LspJsonRpcMessage> Notifications { get; } = new List<LspJsonRpcMessage>();

        private readonly IHost _host;

        public TestHost(ILspModule[]? testModules = null)
        {
            // Build the host with the services
            var hostBuilder = Host.CreateApplicationBuilder();
            hostBuilder.AddLsp([]);

            if (testModules == null)
            {
                // use default language
                hostBuilder.Services.AddSingleton<ILspModule, McsLspModule>();
            }
            else
            {
                foreach (var testModule in testModules)
                {
                    hostBuilder.Services.AddSingleton(testModule);
                }
            }

            // Mock ILspTransport to interact with server from tests
            var testStream = new TestLspTransport(FlushMethodName);
            hostBuilder.Services.RemoveAll<ILspTransport>();
            hostBuilder.Services.AddSingleton<ILspTransport>(testStream);
            hostBuilder.Services.AddSingleton<ILspModule, TestModule>();
            TestStream = testStream;

            // Mock logger: replace all ILogger<> registrations
            hostBuilder.Services.RemoveAll(typeof(ILogger<>));
            hostBuilder.Services.AddSingleton(typeof(ILogger<>), typeof(TestLogger<>));
            hostBuilder.Services.AddSingleton<TestLogger>(Logs);

            // Start the host
            _host = hostBuilder.Build();
            _host.Start();
        }

        public async ValueTask DisposeAsync()
        {
            await _host.StopAsync();
        }

        /// <summary>
        /// Waits for a response from the server, ignoring any notification messages by default.
        /// Any ignored notification messages are stored in <see cref="Notifications"/> property.
        /// </summary>
        /// <param name="includeNotification">Optional argument specifying notification methods to include when receiving message of type <see cref="LspJsonRpcMessage"/>.</param>
        /// <returns>Response message (either notification <see cref="LspJsonRpcMessage"/> or response <see cref="JsonRpcResponse"/>)</returns>
        public async Task<BaseJsonRpcMessage> GetResponseAsync(IEnumerable<string>? includeNotification = null)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
            var msg = await TestStream.ReadMessageAsync(cts.Token);
            includeNotification ??= Array.Empty<string>();
            while (msg is LspJsonRpcMessage lspMessage && !includeNotification.Contains(lspMessage.Method))
            {
                Notifications.Add(lspMessage);
                msg = await TestStream.ReadMessageAsync(cts.Token);
            }

            return msg;
        }

        private const string FlushMethodName = "flush";

        private class TestModule : ILspModule
        {
            public void ConfigureServices(IServiceCollection services)
            {
                services.AddSingleton<IMethodHandler, FlushHandler>();
            }
        }

        [LanguageServerEndpoint(FlushMethodName, LanguageServerConstants.DefaultLanguageName)]
        private class FlushHandler: IRequestHandler<string, RequestContext>
        {
            public bool MutatesSolutionState => true;

            public Task<string> HandleRequestAsync(RequestContext context, CancellationToken cancellationToken)
            {
                return Task.FromResult("done");
            }
        }
    }
}
