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
using System.Collections.Immutable;

namespace Microsoft.CopilotStudio.Sync.TestHarness;

/// <summary>
/// Builds the DI container for the test harness, registering:
/// - Shared library services (CopilotStudio.Sync)
/// - Platform.Content services (from NuGet)
/// - Host-provided stubs with warning-on-call behavior
/// </summary>
internal static class HostServices
{
    public static async Task<ServiceProvider> BuildAsync(Uri environmentUrl)
    {
        var services = new ServiceCollection();

        // 1. Logging (required by Platform.Content's DataverseClient)
        services.AddLogging();

        // 2. Auth provider (interactive browser, silent after first sign-in)
        var authProvider = await AuthProvider.CreateAsync().ConfigureAwait(false);
        services.AddSingleton<ISyncAuthProvider>(authProvider);

        // 3. Dataverse HTTP client accessor (must be registered BEFORE ServiceRegistrations)
        var dataverseAccessor = new DataverseHttpClientAccessor(authProvider);
        dataverseAccessor.SetDataverseUrl(environmentUrl);
        services.AddSingleton<IDataverseHttpClientAccessor>(dataverseAccessor);

        // 4. Platform.Content core services
        ServiceRegistrations.AddServices(services);

        // 5. Progress reporter
        services.AddSingleton<ISyncProgress, ConsoleSyncProgress>();

        // 6. Host-provided stubs (warning-on-call)
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

        // 7. Shared library services
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

    #region Warning-on-call stubs

    private static void WarnStub(string interfaceName, string methodName)
    {
        Console.Error.WriteLine($"[stub] {interfaceName}.{methodName} called — this is a test harness stub");
    }

    private sealed class StubFeatureConfigurationProvider : IFeatureConfigurationProvider
    {
        private int _callCount;

        public IFeatureConfiguration GetConfiguration(Guid azureAdTenantId, string environmentId)
        {
            if (Interlocked.Increment(ref _callCount) == 1)
            {
                WarnStub(nameof(IFeatureConfigurationProvider), nameof(GetConfiguration));
            }

            return new StubFeatureConfiguration();
        }
    }

    private sealed class StubFeatureConfiguration : IFeatureConfiguration
    {
        public bool IsEnvironmentFeatureEnabled(string featureName, bool defaultValue)
        {
            return defaultValue;
        }

        public bool IsTenantFeatureEnabled(string featureName, bool defaultValue)
        {
            return defaultValue;
        }

        public long GetInt64Value(string settingName, long defaultValue)
        {
            return defaultValue;
        }

        public string GetStringValue(string settingName, string defaultValue)
        {
            return defaultValue;
        }
    }

    private sealed class StubAuthoringStatisticLogger : IAuthoringStatisticLogger
    {
        private int _callCount;

        public void LogTelemetry(
            ImmutableArray<BotComponentChange> BotComponentChanges,
            ImmutableArray<EnvironmentVariableChange> EnvironmentVariableChanges,
            ImmutableArray<ConnectionReferenceChange> ConnectionReferenceChanges,
            BotEntity? changedEntity,
            AuthoringOperationType operationType)
        {
            if (Interlocked.Increment(ref _callCount) == 1)
            {
                WarnStub(nameof(IAuthoringStatisticLogger), nameof(LogTelemetry));
            }
        }
    }

    private sealed class StubDataverseUserIdProvider : IDataverseUserIdProvider
    {
        public Task<Guid> GetDataverseUserIdAsync(AuthoringOperationContext context, CancellationToken ct)
        {
            WarnStub(nameof(IDataverseUserIdProvider), nameof(GetDataverseUserIdAsync));
            throw new NotImplementedException("DataverseUserIdProvider is not implemented in the test harness.");
        }

        public Task<Guid> GetAADUserIdAsync(CdsOrganizationInfo organizationInfo, BotReference botReference, Guid systemUserId, CancellationToken ct)
        {
            WarnStub(nameof(IDataverseUserIdProvider), nameof(GetAADUserIdAsync));
            throw new NotImplementedException("DataverseUserIdProvider is not implemented in the test harness.");
        }
    }

    private sealed class StubExpressionSyntaxAnalyzerProvider : IExpressionSyntaxAnalyzerProvider
    {
        private readonly IExpressionSyntaxAnalyzer _analyzer;

        public StubExpressionSyntaxAnalyzerProvider(IServiceProvider sp)
        {
            // Try to resolve PowerFxSyntaxAnalyzer if registered, otherwise use a no-op
            _analyzer = sp.GetService<IExpressionSyntaxAnalyzer>()
                ?? new Agents.ObjectModel.PowerFx.PowerFxSyntaxAnalyzer(new StubFeatureConfiguration());
        }

        public IExpressionSyntaxAnalyzer GetExpressionSyntaxAnalyzer(Guid azureAdTenantId, string environmentId)
        {
            return _analyzer;
        }
    }

    private sealed class StubConnectorDefinitionMetadataService : IConnectorDefinitionMetadataService
    {
        private int _callCount;

        public Task<ConnectorDefinition> CreateConnectorDefinitionAsync(
            AuthoringOperationContextBase authoringOperation,
            ConnectorDefinitionComponent component,
            CancellationToken ct)
        {
            if (Interlocked.Increment(ref _callCount) == 1)
            {
                WarnStub(nameof(IConnectorDefinitionMetadataService), nameof(CreateConnectorDefinitionAsync));
            }

            return Task.FromResult(new ConnectorDefinition(component.ConnectorId));
        }

        public Task<ConnectorDefinition?> GetHostedConnectorDefinitionAsync(
            AuthoringOperationContextBase authoringOperation,
            ConnectorId connectorId,
            CancellationToken ct)
        {
            if (Interlocked.Increment(ref _callCount) == 1)
            {
                WarnStub(nameof(IConnectorDefinitionMetadataService), nameof(GetHostedConnectorDefinitionAsync));
            }

            return Task.FromResult<ConnectorDefinition?>(new ConnectorDefinition(connectorId));
        }
    }

    private sealed class StubPluginEnrichmentService : IPluginEnrichmentService
    {
        public Task<AIPluginOperation> EnrichAIPluginOperationAsync(
            AuthoringOperationContextBase context,
            AIPluginOperation operation,
            CancellationToken ct)
        {
            WarnStub(nameof(IPluginEnrichmentService), nameof(EnrichAIPluginOperationAsync));
            return Task.FromResult(operation);
        }
    }

    private sealed class StubAIModelEnrichmentService : IAIModelEnrichmentService
    {
        public Task<ImmutableArray<AIModelDefinition>> EnrichAIModelsAsync(
            AuthoringOperationContextBase context,
            ImmutableArray<AIModelDefinition> aiModelDefinitions,
            CancellationToken ct)
        {
            WarnStub(nameof(IAIModelEnrichmentService), nameof(EnrichAIModelsAsync));
            return Task.FromResult(aiModelDefinitions);
        }
    }

    private sealed class StubCloudFlowDefinitionEnrichementService : ICloudFlowDefinitionEnrichementService
    {
        public Task<ImmutableArray<CloudFlowDefinition>> GetCloudFlowDefinitionsAsync(
            AuthoringOperationContextBase context,
            IEnumerable<Guid> workflowIds,
            CancellationToken ct)
        {
            WarnStub(nameof(ICloudFlowDefinitionEnrichementService), nameof(GetCloudFlowDefinitionsAsync));
            return Task.FromResult(ImmutableArray<CloudFlowDefinition>.Empty);
        }
    }

    #endregion
}
