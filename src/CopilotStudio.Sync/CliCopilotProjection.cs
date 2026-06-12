// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.ObjectModel.FileProjection;
using Microsoft.CopilotStudio.McsCore;

namespace Microsoft.CopilotStudio.Sync;

/// <summary>
/// Public CliCopilot projection seam for consumers that need authored workspace paths.
/// </summary>
public static class CliCopilotProjection
{
    private static readonly LspComponentPathResolver PathResolver = new();

    /// <summary>
    /// Folders that contain authored CliCopilot component bodies.
    /// </summary>
    public static IReadOnlyList<string> AuthoredComponentBodyFolders { get; } = Array.AsReadOnly(
        LspProjection.CliRules.Values
            .Select(r => r.Folder.TrimEnd('/'))
            .Where(f => f.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray());

    /// <summary>
    /// Gets the authored CliCopilot body path and, for file attachments, payload path.
    /// </summary>
    /// <param name="component">Component to project.</param>
    /// <param name="definition">Definition that provides bot name and parent-agent context.</param>
    public static CliCopilotComponentProjection GetComponentProjection(BotComponentBase component, DefinitionBase definition)
    {
        if (component == null)
        {
            throw new ArgumentNullException(nameof(component));
        }

        if (definition == null)
        {
            throw new ArgumentNullException(nameof(definition));
        }

        var bodyPath = new AgentFilePath(PathResolver.GetComponentPath(component, definition, AuthoringShape.CliCopilot));
        var bodyFolder = NormalizeFolder(bodyPath.ParentDirectoryName);
        var projection = new CliCopilotComponentProjection
        {
            BodyPath = bodyPath.ToString(),
            BodyFolder = bodyFolder,
        };

        if (component is FileAttachmentComponent fileAttachment)
        {
            var payloadFileName = fileAttachment.DisplayName;
            if (!string.IsNullOrEmpty(payloadFileName))
            {
                projection = projection with
                {
                    PayloadFolder = bodyFolder,
                    PayloadPath = CombineRelativePath(bodyFolder, payloadFileName!),
                };
            }
        }

        return projection;
    }

    private static string CombineRelativePath(string folder, string fileName)
    {
        if (string.IsNullOrEmpty(folder))
        {
            return fileName;
        }

        return folder + "/" + fileName;
    }

    private static string NormalizeFolder(string folder)
        => folder.TrimEnd('/', '\\');
}
