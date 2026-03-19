namespace Microsoft.PowerPlatformLS.Impl.PullAgent.DependencyInjection
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.Agents.ObjectModel.Abstractions;
    using Microsoft.Agents.ObjectModel.Expressions;
    using Microsoft.Agents.ObjectModel.PowerFx;
    using Microsoft.Agents.ObjectModel.Telemetry;
    using Microsoft.Agents.Platform.Content;
    using Microsoft.Agents.Platform.Content.Abstractions;
    using Microsoft.Agents.Platform.Content.Internal.Dataverse.SystemUser;
    using Microsoft.Agents.Platform.Content.Internal.Modules;
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.PowerPlatformLS.Contracts.FileLayout;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common.DependencyInjection;
    using Microsoft.PowerPlatformLS.Impl.PullAgent.Auth;
    using Microsoft.PowerPlatformLS.Impl.PullAgent.Dataverse;
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Globalization;
    using System.Threading.Tasks;

    public class PullAgentLspModule : ILspModule
    {
        private readonly BuildVersionInfo _versionInfo;

        public PullAgentLspModule(BuildVersionInfo versionInfo)
        {
            _versionInfo = versionInfo;
        }

        public void ConfigureServices(IServiceCollection services)
        {
            string userAgent = $"MCSVSCode-{_versionInfo.VsixVersion ?? "unknown"}";
            ServiceRegistrations.AddServices(services, userAgent);

            services.AddSingleton<IFeatureConfigurationProvider, FakeFeatureConfigurationProvider>();
            services.AddSingleton<IIslandControlPlaneService, IslandControlPlaneService>();
            services.AddSingleton<IOperationLogger, LspOperationLogger>();
            services.AddSingleton<IAuthoringStatisticLogger, FakeAuthoringStatisticLogger>();
            services.AddSingleton<Func<string, string, DataverseClient>>(sp => (dataverseUrl, accessToken) => new DataverseClient(sp.GetRequiredService<IHttpClientFactory>().CreateClient(), dataverseUrl, accessToken, userAgent));
            services.AddSingleton<IDataverseHttpClientAccessor, DataverseHttpClientAccessor>();
            services.AddSingleton<IDataverseUserIdProvider, DataverseUserIdProvider>();
            services.AddSingleton<IExpressionSyntaxAnalyzer, PowerFxSyntaxAnalyzer>();
            services.AddSingleton<IExpressionSyntaxAnalyzerProvider, PowerFxExpressionSyntaxAnalyzerProvider>();
            services.AddSingleton<IConnectorDefinitionMetadataService, MockConnectorDefinitionMetadataService>();
            services.AddSingleton<IPluginEnrichmentService, MockPluginEnrichmentService>();
            services.AddSingleton<IAIModelEnrichmentService, MockAIModelEnrichmentService>();
            services.AddSingleton<ICloudFlowDefinitionEnrichementService, MockCloudFlowDefinitionEnrichementService>();
            services.AddSingleton<IOperationContextProvider,OperationContextProvider>();
            services.AddSingleton<IFileAccessorFactory, FileAccessorFactory>();
            services.AddSingleton<IWorkspaceSynchronizer, WorkspaceSynchronizer>();
            services.AddSingleton<IComponentPathResolver, LspComponentPathResolver>();
            services.AddTransient<AuthorizeDataverseRequestHandler>();
            services.AddTransient<AuthorizeCopilotStudioRequestHandler>();
            services.AddSingleton<TokenManager>();
            services.AddSingleton<ITokenManager>(sp => sp.GetRequiredService<TokenManager>());
            services.AddSingleton<ITokenProvider>(sp => sp.GetRequiredService<TokenManager>());
            AddHttpClient<AuthorizeDataverseRequestHandler>(HttpClientNames.Dataverse);
            AddHttpClient<AuthorizeCopilotStudioRequestHandler>(HttpClientNames.BotManagement);
            services.AddHttpClient<IDataverseClient, DataverseClient>();

            services.AddSingleton<IMethodHandler, CloneAgentHandler>();
            services.AddSingleton<IMethodHandler, SyncPushHandler>();
            services.AddSingleton<IMethodHandler, SyncPullHandler>();
            services.AddSingleton<IMethodHandler, GetLocalChangeHandler>();
            services.AddSingleton<IMethodHandler, GetRemoteChangeHandler>();
            services.AddSingleton<IMethodHandler, GetRemoteFileHandler>();
            services.AddSingleton<IMethodHandler, GetWorkspaceDetailsHandler>();
            services.AddSingleton<IMethodHandler, ReattachAgentHandler>();

            void AddHttpClient<THandler>(string name) where THandler : DelegatingHandler
            {
                services.AddHttpClient(name)
                    .ConfigurePrimaryHttpMessageHandler(() =>
                    {
                        return new HttpClientHandler()
                        {
                            AllowAutoRedirect = false
                        };
                    })
                    .ConfigureAdditionalHttpMessageHandlers((handlers, sp) =>
                    {
                        handlers.Add(sp.GetRequiredService<THandler>());
                        
                        var logger = sp.GetRequiredService<ILspLogger>();
                        handlers.Add(new LoggingHttpHandler(logger));
                    });
            }
        }

        #region Mocks
        internal class FakeFeatureConfigurationProvider : IFeatureConfigurationProvider
        {
            public IFeatureConfiguration GetConfiguration(Guid azureAdTenantId, string environmentId)
            {
                return new FakeFeatureConfiguration();
            }
        }

        internal class FakeFeatureConfiguration : IFeatureConfiguration
        {
            public CultureInfo? CultureInfoOverride => null;

            public long GetInt64Value(string settingName, long defaultValue) => defaultValue;

            public string GetStringValue(string settingName, string defaultValue) => defaultValue;

            public bool IsEnvironmentFeatureEnabled(string featureName, bool defaultValue) => defaultValue;

            public bool IsTenantFeatureEnabled(string featureName, bool defaultValue) => defaultValue;
        }



        internal class FakeAuthoringStatisticLogger : IAuthoringStatisticLogger
        {
            public void LogTelemetry(
                ImmutableArray<BotComponentChange> BotComponentChanges,
                ImmutableArray<EnvironmentVariableChange> EnvironmentVariableChanges,
                ImmutableArray<ConnectionReferenceChange> ConnectionReferenceChanges,
                BotEntity? changedEntity,
                AuthoringOperationType operationType)
            {
                return;
            }
        }

        internal class DataverseHttpClientAccessor : IDataverseHttpClientAccessor
        {
            private readonly IHttpClientFactory _httpClientFactory;

            public DataverseHttpClientAccessor(IHttpClientFactory httpClientFactory)
            {
                _httpClientFactory = httpClientFactory;
            }

            public HttpClient CreateClient()
            {
                return _httpClientFactory.CreateClient(HttpClientNames.Dataverse);
            }
        }

        internal class DataverseUserIdProvider : IDataverseUserIdProvider
        {
            private readonly IDataverseUserService _dataverseUserService;

            public DataverseUserIdProvider(
                IDataverseUserService dataverseUserService)
            {
                _dataverseUserService = dataverseUserService;
            }

            public async Task<Guid> GetAADUserIdAsync(CdsOrganizationInfo organizationInfo, BotReference botReference, Guid systemUserId, CancellationToken ct)
            {
                return await _dataverseUserService.GetAADUserIdAsync(organizationInfo, botReference, systemUserId, ct);
            }

            public async Task<Guid> GetDataverseUserIdAsync(
                AuthoringOperationContext context,
                CancellationToken ct)
            {
                if (!context.ImpersonatedUser.HasValue || context.ImpersonatedUser.Value.AadUserId == Guid.Empty)
                {
                    return Guid.Empty;
                }

                var aadUserId = context.ImpersonatedUser.Value.AadUserId;
                return await _dataverseUserService.GetSystemUserIdAsync(context, aadUserId, ct);
            }
        }

        class MockConnectorDefinitionMetadataService : IConnectorDefinitionMetadataService
        {
            public Task<ConnectorDefinition> CreateConnectorDefinitionAsync(AuthoringOperationContextBase authoringOperation, ConnectorDefinitionComponent component, CancellationToken ct)
            {
                return Task.FromResult(new ConnectorDefinition(component.ConnectorId));
            }

            public Task<ConnectorDefinition?> GetHostedConnectorDefinitionAsync(AuthoringOperationContextBase authoringOperation, ConnectorId connectorId, CancellationToken ct)
            {
                return Task.FromResult<ConnectorDefinition?>(new ConnectorDefinition(connectorId));
            }
        }

        class MockPluginEnrichmentService : IPluginEnrichmentService
        {
            public Task<AIPluginOperation> EnrichAIPluginOperationAsync(AuthoringOperationContextBase context, AIPluginOperation operation, CancellationToken ct)
            {
                return Task.FromResult(operation);
            }
        }

        internal class MockAIModelEnrichmentService : IAIModelEnrichmentService
        {
            public Task<ImmutableArray<AIModelDefinition>> EnrichAIModelsAsync(AuthoringOperationContextBase context, ImmutableArray<AIModelDefinition> aiModelDefinitions,
                CancellationToken ct)
            {
                return Task.FromResult(aiModelDefinitions);
            }
        }

        internal class MockCloudFlowDefinitionEnrichementService : ICloudFlowDefinitionEnrichementService
        {
            public Task<ImmutableArray<CloudFlowDefinition>> GetCloudFlowDefinitionsAsync(AuthoringOperationContextBase context, IEnumerable<Guid> workflowIds, CancellationToken ct)
            {
                return Task.FromResult(ImmutableArray<CloudFlowDefinition>.Empty);
            }
        }


        internal class PowerFxExpressionSyntaxAnalyzerProvider : IExpressionSyntaxAnalyzerProvider
        {
            public IExpressionSyntaxAnalyzer GetExpressionSyntaxAnalyzer(Guid azureAdTenantId, string environmentId)
            {
                return new PowerFxSyntaxAnalyzer(new FakeFeatureConfiguration());
            }
        }
        #endregion
    }
}
