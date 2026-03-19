namespace Microsoft.PowerPlatformLS.UnitTests.TestUtilities
{
    using System.Threading.Tasks;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common.DependencyInjection;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using System.Threading;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;

    /// <summary>
    /// Add a "pass" method which is handy for making sure previous notifications are processed.
    /// </summary>
    internal class PassRequestModule : ILspModule
    {
        private const string PassMethodName = "pass";

        internal static async Task AssertPassAsync(TestHost context)
        {
            var passRequest = JsonRpc.CreateRequestMessage(PassMethodName, (object?)null);
            context.TestStream.WriteMessage(passRequest);
            var response = await context.GetResponseAsync();
            var passResponse = response as JsonRpcResponse;
            var passResult = JsonRpc.GetValidResult<PassResponse>(passResponse);
            Xunit.Assert.NotNull(passResult);
        }

        public void ConfigureServices(IServiceCollection services)
        {
            services.AddHandler<PassRequestHandler>();
        }

        private class PassResponse
        {
            public string Message { get; set; } = "Success";
        }

        [LanguageServerEndpoint(PassMethodName, LanguageServerConstants.DefaultLanguageName)]
        private class PassRequestHandler : IRequestHandler<PassResponse, RequestContext>
        {
            /// <summary>
            /// Prevent running in parallel with previous methods.
            /// </summary>
            public bool MutatesSolutionState => true;

            public Task<PassResponse> HandleRequestAsync(RequestContext context, CancellationToken cancellationToken)
            {
                return Task.FromResult(new PassResponse());
            }
        }
    }
}
