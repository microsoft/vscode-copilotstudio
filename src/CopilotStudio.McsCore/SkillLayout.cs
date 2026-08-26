// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.ObjectModel.Yaml;

namespace Microsoft.CopilotStudio.McsCore;

internal static class SkillLayout
{
    internal const string AnchorFileName = "skill.mcs.yml";
    internal const string AnchorFileNameWithoutExtension = "skill";
    internal const string ManifestFileName = "SKILL.md";
    internal const string SidecarExtension = ".mcs.yml";
    internal const string BundleMarkerPrefix = "<!-- bic:bundle=";
    internal const string BundleMarkerSuffix = " -->";

    private const string AnchorSuffix = "/" + AnchorFileName;

    internal static string GetSkillFolderPath(string folderName) => $"{LspProjection.BehaviorsFolder}{folderName}";

    internal static AgentFilePath GetAnchorPath(string folderName) => new AgentFilePath($"{LspProjection.BehaviorsFolder}{folderName}{AnchorSuffix}");

    internal static AgentFilePath GetLegacyAnchorPath(string folderName) => new AgentFilePath($"{LspProjection.BehaviorsFolder}{folderName}{SidecarExtension}");

    internal static AgentFilePath GetManifestPath(string folderName) => new AgentFilePath($"{LspProjection.BehaviorsFolder}{folderName}/{ManifestFileName}");

    internal static AgentFilePath GetAssetPath(string folderName, string relativeDisplayName) => new AgentFilePath($"{LspProjection.BehaviorsFolder}{folderName}/{NormalizeRelativeName(relativeDisplayName)}");

    internal static AgentFilePath GetAssetSidecarPath(string folderName, string relativeDisplayName) => new AgentFilePath($"{LspProjection.BehaviorsFolder}{folderName}/{NormalizeRelativeName(relativeDisplayName)}{SidecarExtension}");

    internal static AgentFilePath GetLegacyLinkPath(string folderName) => new AgentFilePath($"{LspProjection.BehaviorsFolder}{folderName}/{SkillLink.LinkFileName}");

    internal static bool IsManifest(string? relativeDisplayName) => string.Equals(NormalizeRelativeName(relativeDisplayName ?? string.Empty), ManifestFileName, StringComparison.OrdinalIgnoreCase);

    internal static string NormalizeRelativeName(string relativeDisplayName)
    {
        var normalized = relativeDisplayName.Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized.Substring(2);
        }

        return normalized.TrimStart('/');
    }

    internal static bool TryGetFolderFromAnchorPath(string pathValue, out string folderName)
    {
        folderName = string.Empty;
        if (!pathValue.StartsWith(LspProjection.BehaviorsFolder, StringComparison.Ordinal))
        {
            return false;
        }

        var remainder = pathValue.Substring(LspProjection.BehaviorsFolder.Length);
        if (remainder.EndsWith(AnchorSuffix, StringComparison.OrdinalIgnoreCase))
        {
            folderName = remainder.Substring(0, remainder.Length - AnchorSuffix.Length);
        }
        else if (remainder.IndexOf('/') < 0 && remainder.EndsWith(SidecarExtension, StringComparison.OrdinalIgnoreCase))
        {
            folderName = remainder.Substring(0, remainder.Length - SidecarExtension.Length);
        }

        return folderName.Length > 0 && folderName.IndexOf('/') < 0;
    }

    internal static IReadOnlyList<string> ListSkillFolders(IFileAccessor fileAccessor)
    {
        var folders = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in fileAccessor.ListFiles(LspProjection.BehaviorsFolder))
        {
            var pathValue = file.ToString();
            if (!pathValue.StartsWith(LspProjection.BehaviorsFolder, StringComparison.Ordinal))
            {
                continue;
            }

            var remainder = pathValue.Substring(LspProjection.BehaviorsFolder.Length);
            var slash = remainder.IndexOf('/');
            string? folderName = null;

            if (slash < 0)
            {
                if (remainder.EndsWith(SidecarExtension, StringComparison.OrdinalIgnoreCase))
                {
                    folderName = remainder.Substring(0, remainder.Length - SidecarExtension.Length);
                }
            }
            else
            {
                var leaf = remainder.Substring(slash + 1);
                if (string.Equals(leaf, AnchorFileName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(leaf, ManifestFileName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(leaf, SkillLink.LinkFileName, StringComparison.OrdinalIgnoreCase))
                {
                    folderName = remainder.Substring(0, slash);
                }
            }

            if (folderName != null && folderName.Length > 0 && seen.Add(folderName))
            {
                folders.Add(folderName);
            }
        }

        return folders;
    }

    internal static bool TryGetAnchorPath(IFileAccessor fileAccessor, string folderName, out AgentFilePath anchorPath)
    {
        anchorPath = GetAnchorPath(folderName);
        if (fileAccessor.Exists(anchorPath))
        {
            return true;
        }

        anchorPath = GetLegacyAnchorPath(folderName);
        return fileAccessor.Exists(anchorPath);
    }

    internal static McsMetadata ReadAnchorMetadata(IFileAccessor fileAccessor, string folderName)
        => TryGetAnchorPath(fileAccessor, folderName, out var anchorPath) ? ReadMetadata(fileAccessor, anchorPath) : default;

    internal static McsMetadata ReadMetadata(IFileAccessor fileAccessor, AgentFilePath path)
    {
        if (!fileAccessor.Exists(path))
        {
            return default;
        }

        string text;
        try
        {
            using var stream = fileAccessor.OpenRead(path);
            using var reader = new StreamReader(stream);
            text = reader.ReadToEnd();
        }
        catch (IOException)
        {
            return default;
        }

        return ParseMetadata(text);
    }

    internal static McsMetadata ParseMetadata(string text)
    {
        try
        {
            var metadata = McsFileParserCore.ReadMcsMetadata(YamlSerializer.Deserialize<BotElement>(text));
            return metadata.Equals(default(McsMetadata)) ? McsFileParserCore.ReadMcsMetadata(YamlSerializer.Deserialize<FileAttachmentComponentMetadata>(text)) : metadata;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return default;
        }
    }

    internal static string BuildBundleMarker(string bundleSchemaName) => $"{BundleMarkerPrefix}{bundleSchemaName}{BundleMarkerSuffix}";

    internal static IReadOnlyList<string> ListAssets(IFileAccessor fileAccessor, string folderName)
    {
        var assets = new List<string>();
        var folderPrefix = $"{GetSkillFolderPath(folderName)}/";

        foreach (var file in fileAccessor.ListFiles(GetSkillFolderPath(folderName)))
        {
            var pathValue = file.ToString();
            if (!pathValue.StartsWith(folderPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relativeName = pathValue.Substring(folderPrefix.Length);
            if (string.Equals(relativeName, AnchorFileName, StringComparison.Ordinal)
                || string.Equals(relativeName, ManifestFileName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(relativeName, ManifestFileName + SidecarExtension, StringComparison.OrdinalIgnoreCase)
                || string.Equals(relativeName, SkillLink.LinkFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            assets.Add(relativeName.EndsWith(SidecarExtension, StringComparison.OrdinalIgnoreCase) ? relativeName.Substring(0, relativeName.Length - SidecarExtension.Length) : relativeName);
        }

        return assets;
    }

    internal static bool HasAssets(IFileAccessor fileAccessor, string folderName) => ListAssets(fileAccessor, folderName).Count > 0;

    internal static bool HasManifestFile(IFileAccessor fileAccessor, string folderName) => fileAccessor.Exists(GetManifestPath(folderName));

    internal static string? ResolveContent(IFileAccessor fileAccessor, string folderName, string? currentContent, bool manifestIsComponent)
    {
        var bundle = ReadAnchorMetadata(fileAccessor, folderName).Bundle ?? TryGetBundleFromMarker(currentContent);
        if (bundle != null)
        {
            return BuildBundleMarker(bundle);
        }

        return manifestIsComponent ? currentContent : ReadManifestText(fileAccessor, folderName) ?? currentContent;
    }

    internal static string? ReadManifestText(IFileAccessor fileAccessor, string folderName)
    {
        var manifestPath = GetManifestPath(folderName);
        if (!fileAccessor.Exists(manifestPath))
        {
            return null;
        }

        try
        {
            using var stream = fileAccessor.OpenRead(manifestPath);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (IOException)
        {
            return null;
        }
    }

    internal static string? TryGetBundleFromMarker(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return null;
        }

        var start = content!.IndexOf(BundleMarkerPrefix, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += BundleMarkerPrefix.Length;
        var end = content.IndexOf("-->", start, StringComparison.Ordinal);
        if (end < 0)
        {
            return null;
        }

        var bundle = content.Substring(start, end - start).Trim();
        return bundle.Length > 0 ? bundle : null;
    }

    internal static string MintBundleSchemaName(string folderName, string botName)
    {
        var stem = new string(folderName.Where(character => character <= 127 && char.IsLetterOrDigit(character)).ToArray());
        stem = stem.Length == 0 ? "skill" : stem;
        return $"{botName}{LspProjection.FileAttachmentInfix}{stem}{McsFileParserCore.HashStringToGuid(folderName).ToString("N").Substring(0, 8)}zip";
    }
}
