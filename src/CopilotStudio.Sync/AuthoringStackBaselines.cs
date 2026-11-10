// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Collections.Immutable;
using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.ObjectModel.Abstractions;
using Microsoft.Agents.ObjectModel.Expressions;
using Microsoft.Agents.ObjectModel.PowerFx;
using Microsoft.Agents.ObjectModel.Telemetry;
using Microsoft.Agents.Platform.Content;
using Microsoft.Agents.Platform.Content.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.CopilotStudio.Sync;

/// <summary>Feature configuration that answers every question with the caller's default.</summary>
internal sealed class BaselineFeatureConfiguration : IFeatureConfiguration
{
    /// <inheritdoc />
    public long GetInt64Value(string settingName, long defaultValue) => defaultValue;

    /// <inheritdoc />
    public string GetStringValue(string settingName, string defaultValue) => defaultValue;

    /// <inheritdoc />
    public bool IsEnvironmentFeatureEnabled(string featureName, bool defaultValue) => defaultValue;

    /// <inheritdoc />
    public bool IsTenantFeatureEnabled(string featureName, bool defaultValue) => defaultValue;
}

/// <summary>Supplies <see cref="BaselineFeatureConfiguration"/> for every tenant and environment.</summary>
internal sealed class BaselineFeatureConfigurationProvider : IFeatureConfigurationProvider
{
    /// <inheritdoc />
    public IFeatureConfiguration GetConfiguration(Guid azureAdTenantId, string environmentId) => new BaselineFeatureConfiguration();
}

/// <summary>Discards authoring telemetry, which a sync host does not emit.</summary>
internal sealed class NoOperationAuthoringStatisticLogger : IAuthoringStatisticLogger
{
    /// <inheritdoc />
    public void LogTelemetry(ImmutableArray<BotComponentChange> botComponentChanges, ImmutableArray<EnvironmentVariableChange> environmentVariableChanges, ImmutableArray<ConnectionReferenceChange> connectionReferenceChanges, BotEntity? changedEntity, AuthoringOperationType operationType)
    {
    }
}

/// <summary>Returns connector definitions carrying only the connector identifier.</summary>
internal sealed class MinimalConnectorDefinitionMetadataService : IConnectorDefinitionMetadataService
{
    /// <inheritdoc />
    public Task<ConnectorDefinition> CreateConnectorDefinitionAsync(AuthoringOperationContextBase authoringOperation, ConnectorDefinitionComponent component, CancellationToken cancellationToken) => Task.FromResult(new ConnectorDefinition(connectorId: component.ConnectorId));

    /// <inheritdoc />
    public Task<ConnectorDefinition?> GetHostedConnectorDefinitionAsync(AuthoringOperationContextBase authoringOperation, ConnectorId connectorId, CancellationToken cancellationToken) => Task.FromResult<ConnectorDefinition?>(new ConnectorDefinition(connectorId: connectorId));
}

/// <summary>Returns plugin operations unchanged.</summary>
internal sealed class PassThroughPluginEnrichmentService : IPluginEnrichmentService
{
    /// <inheritdoc />
    public Task<AIPluginOperation> EnrichAIPluginOperationAsync(AuthoringOperationContextBase context, AIPluginOperation operation, CancellationToken cancellationToken) => Task.FromResult(operation);
}

/// <summary>Returns model definitions unchanged.</summary>
internal sealed class PassThroughModelEnrichmentService : IAIModelEnrichmentService
{
    /// <inheritdoc />
    public Task<ImmutableArray<AIModelDefinition>> EnrichAIModelsAsync(AuthoringOperationContextBase context, ImmutableArray<AIModelDefinition> aiModelDefinitions, CancellationToken cancellationToken) => Task.FromResult(aiModelDefinitions);
}

/// <summary>Resolves no cloud flow definitions, because workflows are synchronized separately.</summary>
internal sealed class EmptyCloudFlowDefinitionEnrichementService : ICloudFlowDefinitionEnrichementService
{
    /// <inheritdoc />
    public Task<ImmutableArray<CloudFlowDefinition>> GetCloudFlowDefinitionsAsync(AuthoringOperationContextBase context, IEnumerable<Guid> workflowIds, CancellationToken cancellationToken) => Task.FromResult(ImmutableArray<CloudFlowDefinition>.Empty);
}

/// <summary>Loads no skill-resource bodies, because attachment content is not fetched during sync.</summary>
internal sealed class NoSkillResourceContentLoader : ISkillResourceContentLoader
{
    /// <inheritdoc />
    public Task<SkillResource?> LoadAsync(AuthoringOperationContextBase context, FileAttachmentComponent attachment, CancellationToken cancellationToken) => Task.FromResult<SkillResource?>(null);
}

/// <summary>Supplies the Power Fx expression analyzer that validates expressions in components.</summary>
public sealed class PowerFxExpressionSyntaxAnalyzerProvider : IExpressionSyntaxAnalyzerProvider
{
    /// <inheritdoc />
    public IExpressionSyntaxAnalyzer GetExpressionSyntaxAnalyzer(Guid azureAdTenantId, string environmentId) => new PowerFxSyntaxAnalyzer(new BaselineFeatureConfiguration());
}

/// <summary>Refuses to resolve Dataverse user identifiers, which the sync paths never request.</summary>
internal sealed class UnsupportedDataverseUserIdProvider : IDataverseUserIdProvider
{
    private const string Refusal = "Resolving a Dataverse user id is not supported by the Copilot Studio sync host.";

    /// <inheritdoc />
    public Task<Guid> GetAADUserIdAsync(CdsOrganizationInfo organizationInfo, BotReference botReference, Guid systemUserId, CancellationToken cancellationToken) => throw new NotSupportedException(Refusal);

    /// <inheritdoc />
    public Task<Guid> GetDataverseUserIdAsync(AuthoringOperationContext context, CancellationToken cancellationToken) => throw new NotSupportedException(Refusal);
}

/// <summary>Registers the baseline authoring-stack services the sync library resolves but does not register itself.</summary>
public static class AuthoringStackBaselineServiceCollectionExtensions
{
    /// <summary>Registers the baseline feature configuration, no-op telemetry and enrichment, the Power Fx analyzer and the unsupported user-id provider.</summary>
    /// <param name="services">The container to register into.</param>
    /// <returns>The same container, for chaining.</returns>
    public static IServiceCollection AddAuthoringStackBaselines(this IServiceCollection services)
    {
        if (services is null)
        {
            throw new ArgumentNullException(nameof(services));
        }

        services.AddSingleton<IOperationLogger, NullOperationLogger>();
        services.AddSingleton<IFeatureConfigurationProvider, BaselineFeatureConfigurationProvider>();
        services.AddSingleton<IFeatureConfiguration, BaselineFeatureConfiguration>();
        services.AddSingleton<IAuthoringStatisticLogger, NoOperationAuthoringStatisticLogger>();
        services.AddSingleton<IConnectorDefinitionMetadataService, MinimalConnectorDefinitionMetadataService>();
        services.AddSingleton<IPluginEnrichmentService, PassThroughPluginEnrichmentService>();
        services.AddSingleton<IAIModelEnrichmentService, PassThroughModelEnrichmentService>();
        services.AddSingleton<ICloudFlowDefinitionEnrichementService, EmptyCloudFlowDefinitionEnrichementService>();
        services.AddSingleton<ISkillResourceContentLoader, NoSkillResourceContentLoader>();
        services.AddSingleton<IExpressionSyntaxAnalyzer>(_ => new PowerFxSyntaxAnalyzer(new BaselineFeatureConfiguration()));
        services.AddSingleton<IExpressionSyntaxAnalyzerProvider, PowerFxExpressionSyntaxAnalyzerProvider>();
        services.AddSingleton<IDataverseUserIdProvider, UnsupportedDataverseUserIdProvider>();

        return services;
    }
}
