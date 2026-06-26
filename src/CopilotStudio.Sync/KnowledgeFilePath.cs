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
        var parentDirectory = PathHelper.ToInternalCanonicalFolderPath(componentPath.ParentDirectoryName);
        var localDisplayName = NormalizeDisplayName(displayName);
        var relativePath = string.IsNullOrEmpty(parentDirectory) ? localDisplayName : $"{parentDirectory}/{localDisplayName}";
        return new AgentFilePath(relativePath);
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
