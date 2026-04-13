// Copyright (C) Microsoft Corporation. All rights reserved.
// Ported from om/src/vscode/LanguageServers/PowerPlatformLS/Contracts.FileLayout/Projectors/LspProjection.cs

using Microsoft.Agents.ObjectModel;
using System.Collections.Frozen;
using System.Linq;

namespace Microsoft.CopilotStudio.Sync;

/// <summary>
/// Declarative projection rules for all MCS element types.
/// </summary>
internal static class LspProjection
{
    internal readonly record struct Rule(string Infix, string Folder, bool DotPassthrough = false, string[]? DotInfixBlocklist = null);

    internal readonly record struct SchemaNameResult(string? SchemaName, bool PreserveQualifiedSchemaName);

    internal static readonly (string Folder, string Infix) DefaultDialogProjection = ("dialogs/", ".dialog.");

    internal static readonly FrozenDictionary<Type, Rule> Rules = new Dictionary<Type, Rule>
    {
        { typeof(AdaptiveDialog), new Rule(".topic.", "topics/", true, new[] { "topic" }) },
        { typeof(TaskDialog), new Rule(".action.", "actions/", true, new[] { "action" }) },
        { typeof(AgentDialog), new Rule(".agent.", "agents/", false) },
        { typeof(GptComponentMetadata), new Rule(".gpt.", "", false) },
        { typeof(GptComponent), new Rule(".gpt.", "", false) },
        { typeof(KnowledgeSource), new Rule(".knowledge.", "knowledge/", true, new[] { "knowledge", "topic", "action" }) },
        { typeof(KnowledgeSourceConfiguration), new Rule(".knowledge.", "knowledge/", true, new[] { "knowledge", "topic", "action" }) },
        { typeof(KnowledgeSourceComponent), new Rule(".knowledge.", "knowledge/", true, new[] { "knowledge", "topic", "action" }) },
        { typeof(FileAttachmentComponentMetadata), new Rule(".file.", "knowledge/files/", true, new[] { "file" }) },
        { typeof(FileAttachmentComponent), new Rule(".file.", "knowledge/files/", true, new[] { "file" }) },
        { typeof(Variable), new Rule(".GlobalVariableComponent.", "variables/", false) },
        { typeof(GlobalVariableComponent), new Rule(".GlobalVariableComponent.", "variables/", false) },
        { typeof(BotSettingsBase), new Rule(".BotSettingsComponent.", "settings/", false) },
        { typeof(BotSettingsComponent), new Rule(".BotSettingsComponent.", "settings/", false) },
        { typeof(Entity), new Rule(".entity.", "entities/", false) },
        { typeof(EntityWithAnnotatedSamples), new Rule(".entity.", "entities/", false) },
        { typeof(CustomEntityComponent), new Rule(".entity.", "entities/", false) },
        { typeof(ExternalTriggerConfiguration), new Rule(".ExternalTriggerComponent.", "trigger/", true, new[] { "ExternalTriggerComponent" }) },
        { typeof(ExternalTriggerComponent), new Rule(".ExternalTriggerComponent.", "trigger/", true, new[] { "ExternalTriggerComponent" }) },
        { typeof(SkillComponent), new Rule(".skill.", "skills/", true, new[] { "skill" }) },
        { typeof(TranslationsComponent), new Rule(".topic.", "translations/", true, new[] { "topic" }) },
        { typeof(LocalizableContentContainer), new Rule(".topic.", "translations/", true, new[] { "topic" }) },
    }.ToFrozenDictionary();

    internal static string? GetRuleInfixForElementType(Type elementType)
    {
        return TryGetRuleForElementType(elementType, out var rule) ? rule.Infix : null;
    }

    internal static string? GetRuleFolderForElementType(Type elementType)
    {
        if (!TryGetRuleForElementType(elementType, out var rule))
        {
            return null;
        }

        return string.IsNullOrEmpty(rule.Folder) ? null : rule.Folder;
    }

    private static readonly FrozenSet<string> ReservedShortNameInfixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".topic.",
        ".GlobalVariableComponent.",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    internal static string? GetSchemaName(string pathWithoutExtension, string? botName, Type elementType)
    {
        return GetSchemaNameResult(pathWithoutExtension, botName, elementType).SchemaName;
    }

    internal static SchemaNameResult GetSchemaNameResult(string pathWithoutExtension, string? botName, Type elementType)
    {
        var normalized = pathWithoutExtension.Replace('\\', '/');
        var fileName = System.IO.Path.GetFileName(normalized);

        if (typeof(GptComponentMetadata).IsAssignableFrom(elementType) ||
            typeof(GptComponent).IsAssignableFrom(elementType))
        {
            return new SchemaNameResult($"{botName}.gpt.default", PreserveQualifiedSchemaName: false);
        }

        if (typeof(AgentDialog).IsAssignableFrom(elementType))
        {
            return new SchemaNameResult(GetAgentDialogSchemaName(normalized, botName), PreserveQualifiedSchemaName: false);
        }

        if (!TryGetRuleForElementType(elementType, out var rule))
        {
            return new SchemaNameResult(null, PreserveQualifiedSchemaName: false);
        }

        if (fileName.Contains('.'))
        {
            if (ShouldExpandDottedName(rule, fileName))
            {
                return new SchemaNameResult(Expand(rule.Infix, fileName, botName), PreserveQualifiedSchemaName: false);
            }

            return new SchemaNameResult(fileName, PreserveQualifiedSchemaName: true);
        }

        return new SchemaNameResult(Expand(rule.Infix, fileName, botName), PreserveQualifiedSchemaName: false);
    }

    [Obsolete("Use the overload with pathWithoutExtension parameter. This method always returns null.")]
    internal static string? GetFilePath(Type elementType, string schemaName, string? botName, string? subAgentFolder)
    {
        return null;
    }

    internal static string? GetFilePath(Type elementType, string schemaName, string? botName, string? subAgentFolder, string? pathWithoutExtension)
    {
        var prefix = subAgentFolder ?? string.Empty;

        if (typeof(GptComponentMetadata).IsAssignableFrom(elementType) ||
            typeof(GptComponent).IsAssignableFrom(elementType))
        {
            return $"{prefix}agent.mcs.yml";
        }

        if (typeof(AgentDialog).IsAssignableFrom(elementType))
        {
            var agentName = DeriveShortName(schemaName, ".agent.", botName);
            return $"{prefix}agents/{agentName}/agent.mcs.yml";
        }

        if (!TryGetRuleForElementType(elementType, out var rule))
        {
            return null;
        }

        if (typeof(AdaptiveDialog).IsAssignableFrom(elementType) && IsTranslationSchemaName(schemaName))
        {
            rule = new Rule(rule.Infix, "translations/", rule.DotPassthrough);
        }

        var allowPreserve = !(subAgentFolder != null
            && (typeof(Variable).IsAssignableFrom(elementType) || typeof(GlobalVariableComponent).IsAssignableFrom(elementType)));

        var preserveQualifiedSchemaName = allowPreserve && IsAlreadyQualifiedPath(rule, pathWithoutExtension);
        var shortName = DeriveShortName(schemaName, rule.Infix, botName, allowPreserve, preserveQualifiedSchemaName);
        return $"{prefix}{rule.Folder}{shortName}.mcs.yml";
    }

    internal static (BotComponentId Id, BotComponentId ParentBotComponentId) GetComponentIds(
        DialogBase dialog,
        BotComponentId? parentId)
    {
        if (dialog is AgentDialog)
        {
            return (parentId ?? default, default);
        }

        return (default, parentId ?? default);
    }

    internal static bool IsTranslationsPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var normalized = path.Replace('\\', '/');
        return normalized.Contains("/translations/", StringComparison.OrdinalIgnoreCase) || normalized.StartsWith("translations/", StringComparison.OrdinalIgnoreCase);
    }

    #region Element Normalization and Type Resolution

    internal static BotElement NormalizeElement(BotElement element)
    {
        if (element is KnowledgeSource source && element is not KnowledgeSourceConfiguration)
        {
            return new KnowledgeSourceConfiguration(source: source);
        }

        if (element is FileAttachmentComponent fac)
        {
            return fac.Metadata ?? new FileAttachmentComponentMetadata();
        }

        return element;
    }

    internal static Type? ResolveTargetComponentType(Type elementType, string? path)
    {
        if (typeof(DialogBase).IsAssignableFrom(elementType) && IsTranslationsPath(path))
        {
            return typeof(TranslationsComponent);
        }

        if (typeof(FileAttachmentComponent).IsAssignableFrom(elementType) ||
            typeof(FileAttachmentComponentMetadata).IsAssignableFrom(elementType))
        {
            return typeof(FileAttachmentComponent);
        }

        if (typeof(KnowledgeSourceConfiguration).IsAssignableFrom(elementType) ||
            typeof(KnowledgeSource).IsAssignableFrom(elementType))
        {
            return typeof(KnowledgeSourceComponent);
        }

        return null;
    }

    #endregion

    internal static (string Folder, string Infix) GetDialogProjection(Type elementType)
    {
        if (typeof(AdaptiveDialog).IsAssignableFrom(elementType))
        {
            return ("topics/", ".topic.");
        }

        if (typeof(TaskDialog).IsAssignableFrom(elementType))
        {
            return ("actions/", ".action.");
        }

        if (typeof(AgentDialog).IsAssignableFrom(elementType))
        {
            return ("agents/", ".agent.");
        }

        return DefaultDialogProjection;
    }

    internal static string GetDialogInfixForFolder(string folderPath)
    {
        var normalized = folderPath.Replace('\\', '/').TrimEnd('/');

        if (normalized.EndsWith("topics", StringComparison.OrdinalIgnoreCase))
        {
            return ".topic.";
        }

        if (normalized.EndsWith("actions", StringComparison.OrdinalIgnoreCase))
        {
            return ".action.";
        }

        if (normalized.IndexOf("agents/", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return ".agent.";
        }

        if (normalized.EndsWith("dialogs", StringComparison.OrdinalIgnoreCase))
        {
            return ".dialog.";
        }

        return DefaultDialogProjection.Infix;
    }

    internal static string GetDialogInfixForFilePath(string filePathWithoutExtension, Type knownElementType)
    {
        var folder = System.IO.Path.GetDirectoryName(filePathWithoutExtension)?.Replace('\\', '/') ?? string.Empty;
        var projection = GetDialogProjection(knownElementType);
        var infix = projection.Infix;

        if (projection.Equals(DefaultDialogProjection))
        {
            infix = GetDialogInfixForFolder(folder);
        }

        return infix;
    }

    internal static bool IsTranslationSchemaName(string? schemaName)
    {
        if (string.IsNullOrEmpty(schemaName)) return false;
        var lastDot = schemaName.LastIndexOf('.');
        if (lastDot < 0 || lastDot == schemaName.Length - 1) return false;

        var suffix = schemaName[(lastDot + 1)..];
        return IsLocaleSuffix(suffix);
    }

    #region Private Helpers

    internal static bool TryGetRuleForElementType(Type elementType, out Rule rule)
    {
        if (Rules.TryGetValue(elementType, out rule))
        {
            return true;
        }

        Type? bestMatch = null;
        Rule bestRule = default;

        foreach (var kvp in Rules)
        {
            if (kvp.Key.IsAssignableFrom(elementType))
            {
                if (bestMatch == null || bestMatch.IsAssignableFrom(kvp.Key))
                {
                    bestMatch = kvp.Key;
                    bestRule = kvp.Value;
                }
            }
        }

        if (bestMatch != null)
        {
            rule = bestRule;
            return true;
        }

        rule = default;
        return false;
    }

    private static string? GetAgentDialogSchemaName(string pathWithoutExtension, string? botName)
    {
        if (!pathWithoutExtension.StartsWith("agents/", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"AgentDialog schema name derivation is only valid for paths under 'agents/'. Path: {pathWithoutExtension}");
        }

        var parts = pathWithoutExtension.Split('/');
        if (parts.Length < 2)
        {
            throw new InvalidOperationException(
                $"Invalid AgentDialog path format. Expected 'agents/{{agentName}}/...' but got: {pathWithoutExtension}");
        }

        var agentName = parts[1];
        return $"{botName}.agent.{agentName}";
    }

    private static string Expand(string infix, string fileName, string? botName)
    {
        return $"{botName}{infix}{fileName}";
    }

    private static bool ShouldExpandDottedName(Rule rule, string fileName)
    {
        if (!rule.DotPassthrough)
        {
            return false;
        }

        var blocklist = rule.DotInfixBlocklist;
        if (blocklist != null && blocklist.Length > 0)
        {
            foreach (var blocked in blocklist)
            {
                if (ContainsSchemaNameReference(fileName, blocked))
                {
                    return false;
                }
            }

            return true;
        }

        var infix = TrimDots(rule.Infix);
        return !ContainsSchemaNameReference(fileName, infix);
    }

    private static string TrimDots(string infixWithDots)
    {
        return infixWithDots.Trim('.');
    }

    private static bool ContainsSchemaNameReference(string value, string infixWithoutDots)
    {
        return value.IndexOf($".{infixWithoutDots}.", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsAlreadyQualifiedPath(Rule rule, string? pathWithoutExtension)
    {
        if (string.IsNullOrEmpty(pathWithoutExtension))
        {
            return false;
        }

        var normalized = pathWithoutExtension.Replace('\\', '/');
        var fileName = System.IO.Path.GetFileName(normalized);
        if (string.IsNullOrEmpty(fileName) || !fileName.Contains('.'))
        {
            return false;
        }

        return !ShouldExpandDottedName(rule, fileName);
    }

    private static string DeriveShortName(
        string? schemaName,
        string infix,
        string? botName,
        bool allowPreserve = true,
        bool preserveQualifiedSchemaName = false)
    {
        if (string.IsNullOrEmpty(schemaName)) return string.Empty;

        if (!string.IsNullOrEmpty(botName) && !schemaName.StartsWith(botName, StringComparison.OrdinalIgnoreCase))
        {
            return schemaName;
        }

        if (allowPreserve && preserveQualifiedSchemaName)
        {
            return schemaName;
        }

        var withoutPrefix = !string.IsNullOrEmpty(botName)
            ? schemaName[botName.Length..]
            : schemaName;

        var infixIndex = withoutPrefix.IndexOf(infix, StringComparison.OrdinalIgnoreCase);
        if (infixIndex >= 0)
        {
            var shortName = withoutPrefix[(infixIndex + infix.Length)..];

            if (IsReservedShortName(shortName, infix))
            {
                return schemaName;
            }

            return shortName;
        }

        return schemaName;
    }

    private static bool IsReservedShortName(string shortName, string infix)
    {
        if (!ReservedShortNameInfixes.Contains(infix))
        {
            return false;
        }

        return shortName.Equals("agent", StringComparison.OrdinalIgnoreCase)
            || shortName.Equals("settings", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLocaleSuffix(string suffix)
    {
        if (suffix.Length != 5 || suffix[2] != '-')
        {
            return false;
        }

        return char.IsLetter(suffix[0])
            && char.IsLetter(suffix[1])
            && char.IsLetter(suffix[3])
            && char.IsLetter(suffix[4]);
    }

    #endregion

    #region Layout Data

    internal static readonly FrozenDictionary<string, Type[]> NonComponentLayoutEntries = new Dictionary<string, Type[]>(StringComparer.OrdinalIgnoreCase)
    {
        { "settings", new[] { typeof(BotEntity) } },
        { "collection", new[] { typeof(BotComponentCollection) } },
        { ".mcs/botdefinition.json", new[] { typeof(DefinitionBase) } },
        { "icon.png", Array.Empty<Type>() },
        { "connectionreferences", new[] { typeof(ConnectionReferencesSourceFile) } },
        { "references", new[] { typeof(ReferencesSourceFile) } },
        { "agent", new[] { typeof(GptComponentMetadata), typeof(AgentDialog) } },
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    internal static readonly FrozenDictionary<string, Type[]> FolderToElementTypes = BuildFolderToElementTypes();

    internal static readonly FrozenDictionary<Type, string[]> ElementTypeToFolders = BuildElementTypeToFolders();

    private static FrozenDictionary<string, Type[]> BuildFolderToElementTypes()
    {
        var map = new Dictionary<string, List<Type>>(StringComparer.OrdinalIgnoreCase);

        foreach (var kvp in NonComponentLayoutEntries)
        {
            map[kvp.Key] = new List<Type>(kvp.Value);
        }

        AddToMap(map, "topics/", typeof(AdaptiveDialog));
        AddToMap(map, "actions/", typeof(TaskDialog));
        AddToMap(map, "translations/", typeof(AdaptiveDialog));
        AddToMap(map, "variables/", typeof(Variable));
        AddToMap(map, "settings/", typeof(BotSettingsBase));
        AddToMap(map, "entities/", typeof(EntityWithAnnotatedSamples));
        AddToMap(map, "knowledge/", typeof(KnowledgeSource));
        AddToMap(map, "knowledge/files/", typeof(FileAttachmentComponent));
        AddToMap(map, "skills/", typeof(SkillDefinition));
        AddToMap(map, "trigger/", typeof(ExternalTriggerConfiguration));
        AddToMap(map, "testcases/", typeof(TestDefinitionBase));

        return map.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToArray()).ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    private static FrozenDictionary<Type, string[]> BuildElementTypeToFolders()
    {
        var reverse = new Dictionary<Type, List<string>>();
        foreach (var kvp in FolderToElementTypes)
        {
            foreach (var type in kvp.Value)
            {
                if (!reverse.TryGetValue(type, out var folders))
                {
                    folders = reverse[type] = new List<string>();
                }
                folders.Add(kvp.Key);
            }
        }
        return reverse.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToArray()).ToFrozenDictionary();
    }

    private static void AddToMap(Dictionary<string, List<Type>> map, string folder, Type elementType)
    {
        if (!map.TryGetValue(folder, out var list))
        {
            list = map[folder] = new List<Type>();
        }
        if (!list.Contains(elementType))
        {
            list.Add(elementType);
        }
    }

    #endregion
}
