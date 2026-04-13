namespace Microsoft.PowerPlatformLS.UnitTests.Impl.PullAgent
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.Agents.Platform.Content;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.DependencyInjection.Extensions;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common.DependencyInjection;
    using Microsoft.PowerPlatformLS.Impl.PullAgent;
    using Microsoft.PowerPlatformLS.Impl.PullAgent.Auth;
    using Microsoft.PowerPlatformLS.UnitTests.TestUtilities;
    using Moq;
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Linq;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;

    internal class PullAgentMockModule : ILspModule
    {
        public InMemoryFileWriter DiskMock { get; } = new();
        public TestHttpMethodHandler HttpClientMock { get; } = new();
        public TestContentAuthoringService ContentAuthoringMock { get; } = new();
        public ITokenProvider? TokenProvider { get; private set; }

        public void ConfigureServices(IServiceCollection services)
        {
            // prevent disk access during tests (both PullAgent and CopilotStudio.Sync interfaces)
            services.RemoveAll<IFileAccessorFactory>();
            services.AddSingleton<IFileAccessorFactory>(DiskMock);
            services.RemoveAll<Microsoft.CopilotStudio.Sync.IFileAccessorFactory>();
            services.AddSingleton<Microsoft.CopilotStudio.Sync.IFileAccessorFactory>(DiskMock);

            // mock network calls
            var httpClient = new HttpClient(HttpClientMock);
            var mockFactory = new Mock<IHttpClientFactory>();
            mockFactory.Setup(_ => _.CreateClient(It.IsAny<string>())).Returns(httpClient);
            services.RemoveAll<IHttpClientFactory>();
            services.AddSingleton(mockFactory.Object);

            // hack TokenManager because the real one is AsyncLocal and it's ignored by the mocked http client
            services.RemoveAll<ITokenProvider>();
            services.RemoveAll<ITokenManager>();
            services.AddSingleton<TestTokenManager>();
            services.AddSingleton<ITokenManager>(p => { var tm = p.GetRequiredService<TestTokenManager>(); TokenProvider = tm; return tm; });
            services.AddSingleton<ITokenProvider>(p => p.GetRequiredService<TestTokenManager>());

            // mock external APIs
            services.RemoveAll<IContentAuthoringService>();
            services.AddSingleton<IContentAuthoringService>(ContentAuthoringMock);
        }

        private class TestTokenManager : ITokenManager, ITokenProvider
        {
            private string? _copilotStudioToken = null;
            private string? _dvToken = null;

            public string GetCopilotStudioToken()
            {
                return _copilotStudioToken ?? throw new InvalidOperationException("Access token is not set.");
            }

            public string GetDataverseToken()
            {
                return _dvToken ?? throw new InvalidOperationException("Access token is not set.");
            }

            public void SetTokens(string dataverseToken, string copilotStudioToken)
            {
                _copilotStudioToken = copilotStudioToken;
                _dvToken = dataverseToken;
            }
        }

        public class TestContentAuthoringService : IContentAuthoringService
        {
            public List<(AuthoringOperationContextBase operationContext, string? changeToken, bool inlcudeAllEnvironmentVariables, bool includeFlowsLinkedToBot)> GetComponentsRequests = new();

            public Task<BotEntity?> GetBotEntityAsync(AuthoringOperationContext operationContext, CancellationToken cancellationToken)
            {
                throw new System.NotImplementedException();
            }

            public Task<(BotEntity?, BotPermissions)> GetBotEntityWithAccessRightsAsync(AuthoringOperationContext operationContext, CancellationToken cancellationToken)
            {
                throw new System.NotImplementedException();
            }

            public Task<PvaComponentChangeSet> GetComponentsAsync(AuthoringOperationContextBase operationContext, string? changeToken, bool inlcudeAllEnvironmentVariables, bool includeFlowsLinkedToBot, CancellationToken cancellationToken)
            {
                GetComponentsRequests.Add((operationContext, changeToken, inlcudeAllEnvironmentVariables, includeFlowsLinkedToBot));
                return Task.FromResult(new PvaComponentChangeSet(Enumerable.Empty<BotComponentChange>(), null, $"{nameof(PullAgentMockModule)} change token"));
            }

            public Task<PvaComponentChangeSet> SaveChangesAsync(AuthoringOperationContextBase operationContext, PvaComponentChangeSet changes, BotEntityTag? tag, bool bypassSynchronization, bool inlcudeAllEnvironmentVariables, bool includeFlowsLinkedToBot, CancellationToken cancellationToken)
            {
                throw new System.NotImplementedException();
            }

            public Task<ImmutableArray<BotEntity>> GetBotEntitiesAsync(
                AuthoringOperationContext operationContext,
                int pageNumber,
                int pageCount,
                List<string>? selectedAttributes,
                bool useComponentModifiedOn,
                CancellationToken cancellationToken)
            {
                throw new System.NotImplementedException(); 
            }

            public Task<BotEntity> UpdateBotEntityAsync(BotEntity entity, ApiManagedPayload apiManagedPayload, BotEntityTag tag, AuthoringOperationContext operationContext, CancellationToken cancellationToken)
            {
                throw new System.NotImplementedException();
            }

            public Task SaveChangesWithoutUpdatedContentRetrieveAsync(AuthoringOperationContextBase operationContext, PvaComponentChangeSet changes, bool bypassSynchronization, CancellationToken cancellationToken)
            {
                throw new NotImplementedException();
            }
        }
    }
}