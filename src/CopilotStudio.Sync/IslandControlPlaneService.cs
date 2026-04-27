// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.ObjectModel.Yaml;
using Microsoft.Agents.Platform.Content;
using System.Threading;

namespace Microsoft.CopilotStudio.Sync;

/// <summary>
/// Sync-component service backed by Dataverse via <see cref="IContentAuthoringService"/>.
/// All component reads and writes flow through Dataverse.
/// </summary>
internal sealed class IslandControlPlaneService : IIslandControlPlaneService
{
    private readonly IContentAuthoringService _contentAuthoringService;

    public IslandControlPlaneService(IContentAuthoringService contentAuthoringService)
    {
        _contentAuthoringService = contentAuthoringService;
    }

    public async Task<PvaComponentChangeSet> SaveChangesAsync(AuthoringOperationContextBase operationContext, PvaComponentChangeSet pushChangeset, CancellationToken cancellationToken)
    {
        using var yamlContext = YamlSerializationContext.UseYamlPassThroughSerializationContext();
        var tag = new BotEntityTag { BypassSynchronization = false, Source = "api" };
        await _contentAuthoringService.SaveChangesAsync(operationContext, pushChangeset, tag, false, false, true, cancellationToken).ConfigureAwait(false);
        return await GetComponentsAsync(operationContext, pushChangeset.ChangeToken, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PvaComponentChangeSet> GetComponentsAsync(
        AuthoringOperationContextBase operationContext,
        string? changeToken,
        CancellationToken cancellationToken)
    {
        using var yamlContext = YamlSerializationContext.UseYamlPassThroughSerializationContext();
        return await _contentAuthoringService.GetComponentsAsync(operationContext, changeToken, true, false, cancellationToken).ConfigureAwait(false);
    }
}
