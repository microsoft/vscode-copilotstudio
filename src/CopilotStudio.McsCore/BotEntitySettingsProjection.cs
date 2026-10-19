// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;

namespace Microsoft.CopilotStudio.McsCore;

/// <summary>
/// Projects a <see cref="BotEntity"/> down to the settings a workspace authors.
/// </summary>
public static class BotEntitySettingsProjection
{
    /// <summary>Projects the entity down to the settings the workspace authors.</summary>
    /// <param name="entity">The entity to project.</param>
    /// <returns>The settings projection, without the service's publish receipt.</returns>
    public static BotEntity WithOnlyAuthoredSettingsProperties(this BotEntity entity)
    {
        if (entity is null)
        {
            throw new ArgumentNullException(nameof(entity));
        }

        var builder = entity.WithOnlySettingsYamlProperties().ToBuilder();
        builder.PublishedOn = null;

        return builder.Build();
    }

    /// <summary>Restores the publish receipt the service reported for the agent.</summary>
    /// <param name="entity">The entity to stamp.</param>
    /// <param name="reported">The entity the service reported, or <see langword="null"/> when it reported none.</param>
    /// <returns>The entity carrying the reported publish receipt.</returns>
    public static BotEntity WithPublishStateFrom(this BotEntity entity, BotEntity? reported)
    {
        if (entity is null)
        {
            throw new ArgumentNullException(nameof(entity));
        }

        var builder = entity.ToBuilder();
        builder.PublishedOn = reported?.PublishedOn;

        return builder.Build();
    }
}
