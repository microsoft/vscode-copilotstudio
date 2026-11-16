// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;

namespace Microsoft.CopilotStudio.McsCore;

/// <summary>Projects authored elements down to the content the service stores, without the workspace's projection metadata.</summary>
public static class McsMetadataProjection
{
    /// <summary>Returns the element without its mcs.metadata block, or the element itself when it carries none.</summary>
    public static BotElement WithoutMcsMetadata(this BotElement element)
    {
        if (element.ExtensionData is not RecordDataValue record || !record.Properties.ContainsKey(McsMetadata.PropertyName))
        {
            return element;
        }

        var remainingProperties = record.Properties.Remove(McsMetadata.PropertyName);
        return element.WithExtensionData(remainingProperties.IsEmpty ? null : new RecordDataValue(remainingProperties));
    }
}
