// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.CopilotStudio.McsCore;
using System.Runtime.InteropServices;

namespace Microsoft.CopilotStudio.Sync;

internal static class KnowledgeFilePath
{
    public static string NormalizeDisplayName(string displayName)
    {
        if (string.IsNullOrEmpty(displayName))
        {
            throw new ArgumentException("Knowledge file display name cannot be empty.", nameof(displayName));
        }

        var normalized = displayName.Replace('\\', '/');
        if (IsRooted(normalized))
        {
            throw new InvalidOperationException($"Knowledge file display name '{displayName}' must be a relative path.");
        }

        var segments = new List<string>();
        foreach (var segment in normalized.Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                throw new InvalidOperationException($"Knowledge file display name '{displayName}' cannot contain parent directory segments.");
            }

            if (segment.IndexOf(':') >= 0)
            {
                throw new InvalidOperationException($"Knowledge file display name '{displayName}' cannot contain ':' characters.");
            }

            segments.Add(segment);
        }

        if (segments.Count == 0)
        {
            throw new InvalidOperationException($"Knowledge file display name '{displayName}' does not identify a file.");
        }

        return string.Join("/", segments);
    }

    public static AgentFilePath GetContentFilePath(AgentFilePath componentPath, string displayName)
    {
        var localDisplayName = NormalizeDisplayName(displayName);
        var componentPathValue = componentPath.ToString();

        if (componentPathValue.EndsWith(SkillLayout.SidecarExtension, StringComparison.OrdinalIgnoreCase))
        {
            var nestedContentPath = componentPathValue.Substring(0, componentPathValue.Length - SkillLayout.SidecarExtension.Length);
            if (nestedContentPath.EndsWith("/" + localDisplayName, StringComparison.OrdinalIgnoreCase) || string.Equals(nestedContentPath, localDisplayName, StringComparison.OrdinalIgnoreCase))
            {
                return new AgentFilePath(nestedContentPath);
            }
        }

        var parentDirectory = PathHelper.ToInternalCanonicalFolderPath(componentPath.ParentDirectoryName);
        return new AgentFilePath(string.IsNullOrEmpty(parentDirectory) ? localDisplayName : $"{parentDirectory}/{localDisplayName}");
    }


    public static string GetContentRootFolder(AgentFilePath componentPath, string displayName)
    {
        var contentPath = GetContentFilePath(componentPath, displayName).ToString();
        var localDisplayName = NormalizeDisplayName(displayName);
        return contentPath.Length > localDisplayName.Length ? contentPath.Substring(0, contentPath.Length - localDisplayName.Length - 1) : string.Empty;
    }

    public static string GetDisplayNameFromContentPath(string folder, AgentFilePath file)
    {
        var normalizedFolder = folder.Replace('\\', '/').Trim('/');
        var normalizedPath = file.ToString().Replace('\\', '/');

        if (!string.IsNullOrEmpty(normalizedFolder)
            && normalizedPath.StartsWith(normalizedFolder + "/", StringComparison.OrdinalIgnoreCase))
        {
            return normalizedPath.Substring(normalizedFolder.Length + 1);
        }

        return file.FileName;
    }

    public static string GetLocalPath(string knowledgeFileFolder, string displayName)
    {
        var root = Path.GetFullPath(knowledgeFileFolder);
        var relativePath = NormalizeDisplayName(displayName).Replace('/', Path.DirectorySeparatorChar);
        var localPath = Path.GetFullPath(Path.Combine(root, relativePath));

        var comparison = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var rootWithSeparator = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!localPath.StartsWith(rootWithSeparator, comparison))
        {
            throw new InvalidOperationException($"Knowledge file display name '{displayName}' resolves outside the knowledge file folder.");
        }

        return localPath;
    }

    private static bool IsRooted(string path)
    {
        return path.StartsWith("/", StringComparison.Ordinal)
            || path.StartsWith("//", StringComparison.Ordinal)
            || (path.Length >= 2 && path[1] == ':');
    }
}
