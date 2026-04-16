// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.ObjectModel.Abstractions;
using Microsoft.Agents.ObjectModel.Expressions;
using Microsoft.Agents.ObjectModel.Telemetry;
using Microsoft.Agents.Platform.Content;
using Microsoft.Agents.Platform.Content.Abstractions;
using Microsoft.Agents.Platform.Content.Internal.Modules;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatformLS.Impl.PullAgent.Auth;
using System.Collections.Immutable;

namespace Microsoft.CopilotStudio.Sync.TestHarness;

/// <summary>
/// Builds a DI container using the extension's actual bridge types (TokenManager,
/// LspSyncAuthProvider, LspDataverseHttpClientAccessor) with a pre-acquired Dataverse
/// token. This mirrors how the VS Code extension wires auth — the only difference
/// from <see cref="HostServices"/> is the auth/HTTP plumbing layer.
///
/// Island control plane is disabled (isIslandPreauthorized: false) — matching PAC
/// behavior. The Island path is an extension-specific cross-validation layer, not
/// required for clone/push/pull correctness.
///
/// Used by the clone-via-bridge command to prove extension-path clone output matches
/// the direct CLI clone output (F3 acceptance criteria).
/// </summary>
internal static class BridgeHostServices
{
    public static ServiceProvider BuildWithBridgeTypes(Uri environmentUrl, string dataverseToken)
    {
        var services = new ServiceCollection();

        // 1. Logging
        services.AddLogging();

        // 2. Extension bridge types (the actual code from Impl.PullAgent)
        //    TokenManager stores the Dataverse token via AsyncLocal — same pattern the
        //    extension uses when the VS Code client passes tokens per-request.
        var tokenManager = new TokenManager();
        tokenManager.SetTokens(dataverseToken, copilotStudioToken: "unused-island-disabled");

        services.AddSingleton<TokenManager>(tokenManager);
        services.AddSingleton<ITokenManager>(tokenManager);
        services.AddSingleton<ITokenProvider>(tokenManager);
        services.AddSingleton<LspSyncAuthProvider>();
        services.AddSingleton<ISyncAuthProvider>(sp => sp.GetRequiredService<LspSyncAuthProvider>());

        var dataverseAccessor = new LspDataverseHttpClientAccessor(
            new LspSyncAuthProvider(tokenManager));
        dataverseAccessor.SetDataverseUrl(environmentUrl);
        services.AddSingleton<LspDataverseHttpClientAccessor>(dataverseAccessor);
        services.AddSingleton<IDataverseHttpClientAccessor>(dataverseAccessor);

        // 3. Platform.Content core services
        ServiceRegistrations.AddServices(services);

        // 4. Progress reporter (ConsoleSyncProgress — no ILspLogger dependency)
        services.AddSingleton<ISyncProgress, ConsoleSyncProgress>();

        // 5. Stubs (same as HostServices — identical to PullAgentLspModule's mocks)
        services.AddSingleton<IFeatureConfigurationProvider, StubFeatureConfigurationProvider>();
        services.AddSingleton<IFeatureConfiguration, StubFeatureConfiguration>();
        services.AddSingleton<IAuthoringStatisticLogger, StubAuthoringStatisticLogger>();
        services.AddSingleton<IOperationLogger>(NullOperationLogger.Instance);
        services.AddSingleton<IDataverseUserIdProvider, StubDataverseUserIdProvider>();
        services.AddSingleton<IExpressionSyntaxAnalyzerProvider, StubExpressionSyntaxAnalyzerProvider>();
        services.AddSingleton<IConnectorDefinitionMetadataService, StubConnectorDefinitionMetadataService>();
        services.AddSingleton<IPluginEnrichmentService, StubPluginEnrichmentService>();
        services.AddSingleton<IAIModelEnrichmentService, StubAIModelEnrichmentService>();
        services.AddSingleton<ICloudFlowDefinitionEnrichementService, StubCloudFlowDefinitionEnrichementService>();

        // 6. Shared library services — isIslandPreauthorized: false matches PAC behavior.
        //    The Island control plane is an extension-specific cross-validation layer;
        //    all data flows through Dataverse regardless.
        services.AddSingleton<IWorkspaceSynchronizer, WorkspaceSynchronizer>();
        services.AddSingleton<IOperationContextProvider, OperationContextProvider>();
        services.AddSingleton<ISyncDataverseClient, SyncDataverseClient>();
        services.AddSingleton<IIslandControlPlaneService, IslandControlPlaneService>();
        services.AddSingleton<IFileAccessorFactory, FileAccessorFactory>();
        services.AddSingleton<IMcsFileParser, SyncMcsFileParser>();
        services.AddSingleton<IComponentPathResolver, LspComponentPathResolver>();
        services.AddSingleton(LspProjectorService.Instance);

        return services.BuildServiceProvider();
    }

    #region Stubs (identical to HostServices)

    private sealed class StubFeatureConfigurationProvider : IFeatureConfigurationProvider
    {
        public IFeatureConfiguration GetConfiguration(Guid azureAdTenantId, string environmentId)
            => new StubFeatureConfiguration();
    }

    private sealed class StubFeatureConfiguration : IFeatureConfiguration
    {
        public bool IsEnvironmentFeatureEnabled(string featureName, bool defaultValue) => defaultValue;
        public bool IsTenantFeatureEnabled(string featureName, bool defaultValue) => defaultValue;
        public long GetInt64Value(string settingName, long defaultValue) => defaultValue;
        public string GetStringValue(string settingName, string defaultValue) => defaultValue;
    }

    private sealed class StubAuthoringStatisticLogger : IAuthoringStatisticLogger
    {
        public void LogTelemetry(
            ImmutableArray<BotComponentChange> BotComponentChanges,
            ImmutableArray<EnvironmentVariableChange> EnvironmentVariableChanges,
            ImmutableArray<ConnectionReferenceChange> ConnectionReferenceChanges,
            BotEntity? changedEntity,
            AuthoringOperationType operationType) { }
    }

    private sealed class StubDataverseUserIdProvider : IDataverseUserIdProvider
    {
        public Task<Guid> GetDataverseUserIdAsync(AuthoringOperationContext context, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Guid> GetAADUserIdAsync(CdsOrganizationInfo organizationInfo, BotReference botReference, Guid systemUserId, CancellationToken ct)
            => throw new NotImplementedException();
    }

    private sealed class StubExpressionSyntaxAnalyzerProvider : IExpressionSyntaxAnalyzerProvider
    {
        private readonly IExpressionSyntaxAnalyzer _analyzer;
        public StubExpressionSyntaxAnalyzerProvider(IServiceProvider sp)
        {
            _analyzer = sp.GetService<IExpressionSyntaxAnalyzer>()
                ?? new Agents.ObjectModel.PowerFx.PowerFxSyntaxAnalyzer(new StubFeatureConfiguration());
        }
        public IExpressionSyntaxAnalyzer GetExpressionSyntaxAnalyzer(Guid azureAdTenantId, string environmentId)
            => _analyzer;
    }

    private sealed class StubConnectorDefinitionMetadataService : IConnectorDefinitionMetadataService
    {
        public Task<ConnectorDefinition> CreateConnectorDefinitionAsync(
            AuthoringOperationContextBase authoringOperation, ConnectorDefinitionComponent component, CancellationToken ct)
            => Task.FromResult(new ConnectorDefinition(component.ConnectorId));
        public Task<ConnectorDefinition?> GetHostedConnectorDefinitionAsync(
            AuthoringOperationContextBase authoringOperation, ConnectorId connectorId, CancellationToken ct)
            => Task.FromResult<ConnectorDefinition?>(new ConnectorDefinition(connectorId));
    }

    private sealed class StubPluginEnrichmentService : IPluginEnrichmentService
    {
        public Task<AIPluginOperation> EnrichAIPluginOperationAsync(
            AuthoringOperationContextBase context, AIPluginOperation operation, CancellationToken ct)
            => Task.FromResult(operation);
    }

    private sealed class StubAIModelEnrichmentService : IAIModelEnrichmentService
    {
        public Task<ImmutableArray<AIModelDefinition>> EnrichAIModelsAsync(
            AuthoringOperationContextBase context, ImmutableArray<AIModelDefinition> aiModelDefinitions, CancellationToken ct)
            => Task.FromResult(aiModelDefinitions);
    }

    private sealed class StubCloudFlowDefinitionEnrichementService : ICloudFlowDefinitionEnrichementService
    {
        public Task<ImmutableArray<CloudFlowDefinition>> GetCloudFlowDefinitionsAsync(
            AuthoringOperationContextBase context, IEnumerable<Guid> workflowIds, CancellationToken ct)
            => Task.FromResult(ImmutableArray<CloudFlowDefinition>.Empty);
    }

    #endregion
}
