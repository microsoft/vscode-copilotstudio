// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.CopilotStudio.McsCore.Yaml;

namespace Microsoft.CopilotStudio.Sync.Dataverse;

/// <summary>Reads and writes the metadata sidecar that accompanies a workflow definition on disk.</summary>
public static class WorkflowMetadataFile
{
    /// <summary>Serializes workflow metadata to the sidecar's YAML form.</summary>
    public static string Write(SyncDataverseClient.WorkflowMetadata metadata)
    {
        if (metadata is null)
        {
            throw new ArgumentNullException(nameof(metadata));
        }

        return McsYamlObjectMapper.Serialize(metadata);
    }

    /// <summary>Reads workflow metadata, rejecting any property the type does not declare.</summary>
    public static SyncDataverseClient.WorkflowMetadata? ReadStrict(string yaml)
    {
        if (yaml is null)
        {
            throw new ArgumentNullException(nameof(yaml));
        }

        return McsYamlObjectMapper.DeserializeStrict<SyncDataverseClient.WorkflowMetadata>(yaml);
    }
}
