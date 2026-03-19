namespace Microsoft.PowerPlatformLS.UnitTests.Impl.Core
{
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common.DependencyInjection;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.UnitTests.TestUtilities;
    using System.Threading;
    using System.Threading.Tasks;
    using Xunit;

    public class ConcurrentMethodsTests
    {
        private const string BlockMethodName = "block";
        private const string PassMethodName = "pass";
        private const string BlockMethodResponse = "block method response";
        private const string PassMethodResponse = "pass method response";


        [Fact]
        public async Task SuccessConcurrentMethodExecution_OnCustomMethods_Async()
        {
            var concurrentMethodsModule = new TestConcurrentMethodLspModule();
            await using var context = new TestHost([concurrentMethodsModule]);
            await context.InitializeLanguageServerAsync();

            // write block first
            context.TestStream.WriteMessage(JsonRpc.CreateRequestMessage(BlockMethodName, (object?)null));
            // write pass second
            context.TestStream.WriteMessage(JsonRpc.CreateRequestMessage(PassMethodName, (object?)null));

            var response = await context.GetResponseAsync() as JsonRpcResponse;
            concurrentMethodsModule.Blocker.Release();
            var result = JsonRpc.GetValidResult<string>(response);

            // expect pass response first
            Assert.Equal(PassMethodResponse, result);

            // expect block response second
            response = await context.GetResponseAsync() as JsonRpcResponse;
            result = JsonRpc.GetValidResult<string>(response);
            Assert.Equal(BlockMethodResponse, result);
        }

        private class TestConcurrentMethodLspModule : ILspModule
        {
            public SemaphoreSlim Blocker { get; } = new SemaphoreSlim(0, 1);

            public void ConfigureServices(IServiceCollection services)
            {
                services.AddSingleton<IMethodHandler>(new BlockMethodHandler(Blocker));
                services.AddSingleton<IMethodHandler>(new PassMethodHandler());
            }
        }

        [LanguageServerEndpoint(BlockMethodName, LanguageServerConstants.DefaultLanguageName)]
        private class BlockMethodHandler : IRequestHandler<string, RequestContext>
        {
            private readonly SemaphoreSlim _blocker;

            public BlockMethodHandler(SemaphoreSlim blocker)
            {
                _blocker = blocker;
            }

            public bool MutatesSolutionState => false;

            public async Task<string> HandleRequestAsync(RequestContext context, CancellationToken cancellationToken)
            {
                await _blocker.WaitAsync();
                return BlockMethodResponse;
            }
        }

        [LanguageServerEndpoint(PassMethodName, LanguageServerConstants.DefaultLanguageName)]
        private class PassMethodHandler : IRequestHandler<string, RequestContext>
        {
            public bool MutatesSolutionState => false;

            public Task<string> HandleRequestAsync(RequestContext context, CancellationToken cancellationToken)
            {
                return Task.FromResult(PassMethodResponse);
            }
        }
    }
}
