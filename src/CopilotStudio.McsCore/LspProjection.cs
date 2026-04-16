// Copyright (C) Microsoft Corporation. All rights reserved.
// Ported from om/src/vscode/LanguageServers/PowerPlatformLS/Contracts.FileLayout/Projectors/LspProjection.cs

using Microsoft.Agents.ObjectModel;
using System.Collections.Frozen;
using System.Linq;

namespace Microsoft.CopilotStudio.McsCore;

/// <summary>
/// Declarative projection rules for all MCS element types.
/// </summary>
/// <remarks>
/// <para>This static class consolidates all element-type-to-projection mappings in one place,
/// including legacy component overrides for layout purposes.</para>
/// <para><b>Schema name format:</b> {botName}{Infix}{shortName}</para>
/// <para><b>File path format:</b> {subAgentFolder}{Folder}{shortName}.mcs.yml</para>
/// </remarks>
internal static class LspProjection
{
    /// <summary>
    /// Conditional projection override for a rule.
    /// </summary>
    /// <param name="Infix">Override schema name infix including dots, e.g. ".topic."</param>
    /// <param name="Folder">Override file folder path with trailing slash, e.g. "topics/"</param>
    /// <param name="DotInfixBlocklist">
    /// Optional list of infix tokens (without dots) that, if present in the filename,
    /// indicate the filename is already qualified and should not be expanded.
    /// </param>
    /// <param name="Predicate">
    /// Optional function used to determine whether this override applies for a given file or schema.
    /// It receives:
    /// - pathWithoutExtension (string?): file path without extension
    /// - schemaName (string?): full schema name
    /// Returns true if the override should be used, otherwise false.
    /// If null, the override always applies.
    /// </param>
    internal readonly record struct RuleOverride(string Infix, string Folder, string[]? DotInfixBlocklist = null, Func<string?, string?, bool>? Predicate = null);

    /// <summary>
    /// Projection rule for an element type.
    /// </summary>
    /// <param name="Infix">Schema name infix including dots, e.g. ".topic."</param>
    /// <param name="Folder">File folder path with trailing slash, e.g. "topics/"</param>
    /// <param name="DotPassthrough">If true, dotted filenames may be expanded instead of passed through</param>
    /// <param name="DotInfixBlocklist">
    /// Optional list of infix tokens (without dots) that, if present in the filename,
    /// indicate the filename is already qualified and should not be expanded.
    /// </param>
    /// <param name="Overrides">
    /// Optional ordered override list. The first matching override replaces infix/folder/blocklist
    /// for the current path or schema while preserving <paramref name="DotPassthrough"/>.
    /// </param>
    internal readonly record struct Rule(string Infix, string Folder, bool DotPassthrough = false, string[]? DotInfixBlocklist = null, RuleOverride[]? Overrides = null);

    /// <summary>
    /// Result for schema name derivation that carries whether qualified names should be preserved.
    /// </summary>
    internal readonly record struct SchemaNameResult(string? SchemaName, bool PreserveQualifiedSchemaName);


    internal static readonly (string Folder, string Infix) DefaultDialogProjection = ("dialogs/", ".dialog.");

    /// <summary>
    /// All projection rules keyed by element type.
    /// </summary>
    internal static readonly FrozenDictionary<Type, Rule> Rules = new Dictionary<Type, Rule>
        {
            // Topics can have dots in display names, so DotPassthrough=true
            {
                typeof(AdaptiveDialog),
                new Rule(".topic.", "topics/", true, new[] { "topic" })
            },
            {
                typeof(TaskDialog),
                new Rule(
                    ".action.",
                    "actions/",
                    true,
                    new[] { "action" },
                    new[]
                    {
                        new RuleOverride(
                            ".InvokeConnectedAgentTaskAction.",
                            "agents/",
                            new[] { "InvokeConnectedAgentTaskAction" },
                            (path, schema) => (path?.StartsWith("agents/", StringComparison.OrdinalIgnoreCase) == true
                                               && path?.Contains("/actions/", StringComparison.OrdinalIgnoreCase) != true)
                                              || schema?.Contains(".InvokeConnectedAgentTaskAction.", StringComparison.OrdinalIgnoreCase) == true)
                    })
            },

            // Agent dialogs
            {
                typeof(AgentDialog),
                new Rule(".agent.", "agents/", false)
            },

            // GPT
            {
                typeof(GptComponentMetadata),
                new Rule(".gpt.", "", false)
            },
            {
                typeof(GptComponent),
                new Rule(".gpt.", "", false)
            },

            // Knowledge
            {
                typeof(KnowledgeSource),
                new Rule(".knowledge.", "knowledge/", true, new[] { "knowledge", "topic", "action" })
            },
            {
                typeof(KnowledgeSourceConfiguration),
                new Rule(".knowledge.", "knowledge/", true, new[] { "knowledge", "topic", "action" })
            },
            {
                typeof(KnowledgeSourceComponent),
                new Rule(".knowledge.", "knowledge/", true, new[] { "knowledge", "topic", "action" })
            },

            // File attachments
            {
                typeof(FileAttachmentComponentMetadata),
                new Rule(".file.", "knowledge/files/", true, new[] { "file" })
            },
            {
                typeof(FileAttachmentComponent),
                new Rule(".file.", "knowledge/files/", true, new[] { "file" })
            },

            // Variables
            {
                typeof(Variable),
                new Rule(".GlobalVariableComponent.", "variables/", false)
            },
            {
                typeof(GlobalVariableComponent),
                new Rule(".GlobalVariableComponent.", "variables/", false)
            },

            // Settings
            {
                typeof(BotSettingsBase),
                new Rule(".BotSettingsComponent.", "settings/", false)
            },
            {
                typeof(BotSettingsComponent),
                new Rule(".BotSettingsComponent.", "settings/", false)
            },

            // Entities
            {
                typeof(Entity),
                new Rule(".entity.", "entities/", false)
            },
            {
                typeof(EntityWithAnnotatedSamples),
                new Rule(".entity.", "entities/", false)
            },
            {
                typeof(CustomEntityComponent),
                new Rule(".entity.", "entities/", false)
            },

            // External triggers
            {
                typeof(ExternalTriggerConfiguration),
                new Rule(".ExternalTriggerComponent.", "trigger/", true, new[] { "ExternalTriggerComponent" })
            },
            {
                typeof(ExternalTriggerComponent),
                new Rule(".ExternalTriggerComponent.", "trigger/", true, new[] { "ExternalTriggerComponent" })
            },

            // Skills
            {
                typeof(SkillComponent),
                new Rule(".skill.", "skills/", true, new[] { "skill" })
            },

            // Translations (locale-aware, dot passthrough)
            {
                typeof(TranslationsComponent),
                new Rule(".topic.", "translations/", true, new[] { "topic" })
            },
            {
                typeof(LocalizableContentContainer),
                new Rule(".topic.", "translations/", true, new[] { "topic" })
            },
            { 
                typeof(CustomMetricDefinitionComponent), 
                new Rule(".custommetric.", "custommetrics/", true, new[] { "custommetric" }) 
            },
            { 
                typeof(AgentSkillComponent), 
                new Rule(".agentskill.", "agentskills/", true, new[] { "agentskill" }) 
            },
        }.ToFrozenDictionary();

    /// <summary>
    /// Gets the rule infix for an element type, if one exists.
    /// </summary>
    internal static string? GetRuleInfixForElementType(Type elementType)
    {
        return TryGetRuleForElementType(elementType, null, null, out var rule) ? rule.Infix : null;
    }

    /// <summary>
    /// Gets the rule folder for an element type, if one exists.
    /// </summary>
    internal static string? GetRuleFolderForElementType(Type elementType)
    {
        if (!TryGetRuleForElementType(elementType, null, null, out var rule))
        {
            return null;
        }

        return string.IsNullOrEmpty(rule.Folder) ? null : rule.Folder;
    }

    /// <summary>
    /// Infixes where reserved short names should not be shortened.
    /// </summary>
    private static readonly FrozenSet<string> ReservedShortNameInfixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".topic.",
            ".GlobalVariableComponent.",
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the schema name for a file, applying legacy special cases.
    /// </summary>
    /// <param name="pathWithoutExtension">File path without extension.</param>
    /// <param name="botName">Bot entity schema name prefix.</param>
    /// <param name="elementType">Known element type for the file.</param>
    /// <returns>Full schema name (e.g., "botName.topic.MyTopic").</returns>
    internal static string? GetSchemaName(string pathWithoutExtension, string? botName, Type elementType)
    {
        return GetSchemaNameResult(pathWithoutExtension, botName, elementType).SchemaName;
    }

    internal static SchemaNameResult GetSchemaNameResult(string pathWithoutExtension, string? botName, Type elementType)
    {
        var normalized = pathWithoutExtension.Replace('\\', '/');
        var fileName = System.IO.Path.GetFileName(normalized);

        // GPT special case: always "{botName}.gpt.default"
        if (typeof(GptComponentMetadata).IsAssignableFrom(elementType) ||
            typeof(GptComponent).IsAssignableFrom(elementType))
        {
            return new SchemaNameResult($"{botName}.gpt.default", PreserveQualifiedSchemaName: false);
        }

        // AgentDialog special case: derive from folder path, not filename
        if (typeof(AgentDialog).IsAssignableFrom(elementType))
        {
            return new SchemaNameResult(GetAgentDialogSchemaName(normalized, botName), PreserveQualifiedSchemaName: false);
        }

        // Look up rule for element type
        if (!TryGetRuleForElementType(elementType, null, normalized, out var rule))
        {
            // No rule found - return null to let caller fall back
            return new SchemaNameResult(null, PreserveQualifiedSchemaName: false);
        }

        // Dot handling: follow legacy rules for dotted filenames
        if (fileName.Contains('.'))
        {
            if (ShouldExpandDottedName(rule, fileName))
            {
                return new SchemaNameResult(Expand(rule.Infix, fileName, botName), PreserveQualifiedSchemaName: false);
            }

            return new SchemaNameResult(fileName, PreserveQualifiedSchemaName: true);
        }

        // Standard expansion
        return new SchemaNameResult(Expand(rule.Infix, fileName, botName), PreserveQualifiedSchemaName: false);
    }

    /// <summary>
    /// Gets file path for a component.
    /// </summary>
    /// <param name="elementType">Element type for folder/infix lookup.</param>
    /// <param name="schemaName">Full schema name.</param>
    /// <param name="botName">Bot name prefix.</param>
    /// <param name="subAgentFolder">Sub-agent folder prefix (e.g., "agents/MyAgent/").</param>
    /// <returns>Always returns null. Use <see cref="GetFilePath(Type, string, string?, string?, string?)"/> instead.</returns>
    /// <remarks>
    /// This overload cannot reliably determine the file path because it lacks the original
    /// file path context needed to distinguish between short names and already-qualified names.
    /// </remarks>
    [Obsolete("Use the overload with pathWithoutExtension parameter. This method always returns null.")]
    internal static string? GetFilePath(Type elementType, string schemaName, string? botName, string? subAgentFolder)
    {
        // Path-less projections are ambiguous for qualified filenames; use the path-aware overload instead.
        return null;
    }

    internal static string? GetFilePath(Type elementType, string schemaName, string? botName, string? subAgentFolder, string? pathWithoutExtension)
    {
        var prefix = subAgentFolder ?? string.Empty;

        // GPT: always at agent.mcs.yml
        if (typeof(GptComponentMetadata).IsAssignableFrom(elementType) ||
            typeof(GptComponent).IsAssignableFrom(elementType))
        {
            return $"{prefix}agent.mcs.yml";
        }

        // AgentDialog: agents/{agentName}/agent.mcs.yml
        if (typeof(AgentDialog).IsAssignableFrom(elementType))
        {
            var agentName = DeriveShortName(schemaName, ".agent.", botName);
            return $"{prefix}agents/{agentName}/agent.mcs.yml";
        }

        // Look up rule
        if (!TryGetRuleForElementType(elementType, schemaName, pathWithoutExtension, out var rule))
        {
            return null;
        }

        // Translations: AdaptiveDialog schema names with locale suffix should stay in translations/
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

    /// <summary>
    /// Gets component ID and parent ID for a dialog element.
    /// </summary>
    /// <remarks>
    /// <para>AgentDialog: uses parentId as its own ID with no parent reference.
    /// Other dialogs: use parentId as parent reference with default ID.</para>
    /// </remarks>
    internal static (BotComponentId Id, BotComponentId ParentBotComponentId) GetComponentIds(
        DialogBase dialog,
        BotComponentId? parentId)
    {
        if (dialog is AgentDialog)
        {
            // AgentDialog: id = parentId, no parent reference
            return (parentId ?? default, default);
        }

        // Other dialogs: default id, parentId as parent reference
        return (default, parentId ?? default);
    }

    /// <summary>
    /// Checks if a path is under the translations folder.
    /// </summary>
    internal static bool IsTranslationsPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var normalized = path.Replace('\\', '/').ToLowerInvariant();
        return normalized.Contains("/translations/") || normalized.StartsWith("translations/");
    }

    #region Element Normalization and Type Resolution

    /// <summary>
    /// Normalizes an element before passing to projector.CreateComponent().
    /// </summary>
    /// <remarks>
    /// <para>Some element types need to be wrapped or transformed before component creation:</para>
    /// <list type="bullet">
    /// <item>KnowledgeSource → KnowledgeSourceConfiguration</item>
    /// <item>FileAttachmentComponent → FileAttachmentComponentMetadata</item>
    /// </list>
    /// </remarks>
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

    /// <summary>
    /// Resolves the target component type for an element type, applying normalization rules.
    /// </summary>
    /// <remarks>
    /// <para>Handles special routing cases:</para>
    /// <list type="bullet">
    /// <item>DialogBase in translations/ folder → TranslationsComponent</item>
    /// <item>FileAttachmentComponent/Metadata → FileAttachmentComponent</item>
    /// <item>KnowledgeSource/Configuration → KnowledgeSourceComponent</item>
    /// </list>
    /// </remarks>
    internal static Type? ResolveTargetComponentType(Type elementType, string? path)
    {
        // Translations: route DialogBase to TranslationsComponent when in translations/ folder
        if (typeof(DialogBase).IsAssignableFrom(elementType) && IsTranslationsPath(path))
        {
            return typeof(TranslationsComponent);
        }

        // FileAttachment: normalize both component and metadata types
        if (typeof(FileAttachmentComponent).IsAssignableFrom(elementType) ||
            typeof(FileAttachmentComponentMetadata).IsAssignableFrom(elementType))
        {
            return typeof(FileAttachmentComponent);
        }

        // KnowledgeSource: normalize config type
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

    internal static bool TryGetRuleForElementType(Type elementType, string? schemaName, string? path, out Rule rule)
    {
        if (Rules.TryGetValue(elementType, out var exactRule))
        {
            rule = ResolveRule(exactRule, path, schemaName);
            return true;
        }

        // Check assignability for derived types, preferring more specific types
        // Track the most specific match (deepest in inheritance hierarchy)
        Type? bestMatch = null;
        Rule bestRule = default;

        foreach (var kvp in Rules)
        {
            if (kvp.Key.IsAssignableFrom(elementType))
            {
                // If this is the first match, or if this type is more specific than current best
                if (bestMatch == null || bestMatch.IsAssignableFrom(kvp.Key))
                {
                    bestMatch = kvp.Key;
                    bestRule = ResolveRule(kvp.Value, path, schemaName);
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

    private static Rule ResolveRule(Rule rule, string? path, string? schemaName)
    {
        path = path?.Replace('\\', '/');

        var overrides = rule.Overrides;
        if (overrides != null)
        {
            foreach (var ruleOverride in overrides)
            {
                if (ruleOverride.Predicate == null || ruleOverride.Predicate(path, schemaName))
                {
                    return new Rule(
                        ruleOverride.Infix,
                        ruleOverride.Folder,
                        rule.DotPassthrough,
                        ruleOverride.DotInfixBlocklist ?? rule.DotInfixBlocklist);
                }
            }
        }

        return new Rule(rule.Infix, rule.Folder, rule.DotPassthrough, rule.DotInfixBlocklist);
    }

    private static string? GetAgentDialogSchemaName(string pathWithoutExtension, string? botName)
    {
        // Path should be: agents/{agentName}/agent
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
        // Infix already includes dots, e.g. ".topic."
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

        // If schema doesn't start with bot name, return unchanged
        if (!string.IsNullOrEmpty(botName) && !schemaName.StartsWith(botName, StringComparison.OrdinalIgnoreCase))
        {
            return schemaName;
        }

        // Preserve fully-qualified schema names if they were already qualified in the file name.
        if (allowPreserve && preserveQualifiedSchemaName)
        {
            return schemaName;
        }

        // Remove bot prefix
        var withoutPrefix = !string.IsNullOrEmpty(botName)
            ? schemaName[botName.Length..]
            : schemaName;

        // Find and remove the infix
        var infixIndex = withoutPrefix.IndexOf(infix, StringComparison.OrdinalIgnoreCase);
        if (infixIndex >= 0)
        {
            var shortName = withoutPrefix[(infixIndex + infix.Length)..];

            // Reserved filenames should not be shortened.
            if (IsReservedShortName(shortName, infix))
            {
                return schemaName;
            }

            return shortName;
        }

        // Infix not found - schema name doesn't follow expected pattern.
        // Return original schemaName unchanged (e.g., already-qualified names
        // that passed through GetSchemaName without expansion).
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

    #region Layout Data (Single Source for FileStructureMap)

    /// <summary>
    /// Non-component file entries for the layout map.
    /// These are files/folders that don't map to BotComponentBase subtypes.
    /// </summary>
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

    /// <summary>
    /// Folder → element types mapping for file structure.
    /// Derived from Rules with legacy overrides applied.
    /// </summary>
    /// <remarks>
    /// Key = folder path (e.g., "topics/", "agent")
    /// Value = element types that can appear in that folder
    /// </remarks>
    internal static readonly FrozenDictionary<string, Type[]> FolderToElementTypes = BuildFolderToElementTypes();

    /// <summary>
    /// Element type → folder candidates mapping (reverse of FolderToElementTypes).
    /// </summary>
    internal static readonly FrozenDictionary<Type, string[]> ElementTypeToFolders = BuildElementTypeToFolders();

    private static FrozenDictionary<string, Type[]> BuildFolderToElementTypes()
    {
        var map = new Dictionary<string, List<Type>>(StringComparer.OrdinalIgnoreCase);

        // Add non-component entries
        foreach (var kvp in NonComponentLayoutEntries)
        {
            map[kvp.Key] = new List<Type>(kvp.Value);
        }

        // Add folder mappings from Rules (legacy element types only)
        // Topics
        AddToMap(map, "topics/", typeof(AdaptiveDialog));
        // Actions
        AddToMap(map, "actions/", typeof(TaskDialog));
        // Connected Agent
        AddToMap(map, "agents/", typeof(TaskDialog));
        // Translations - uses AdaptiveDialog, not LocalizableContentContainer
        AddToMap(map, "translations/", typeof(AdaptiveDialog));
        // Variables
        AddToMap(map, "variables/", typeof(Variable));
        // Settings
        AddToMap(map, "settings/", typeof(BotSettingsBase));
        // Entities - uses EntityWithAnnotatedSamples, not Entity
        AddToMap(map, "entities/", typeof(EntityWithAnnotatedSamples));
        // Knowledge - uses KnowledgeSource, not KnowledgeSourceConfiguration
        AddToMap(map, "knowledge/", typeof(KnowledgeSource));
        // File attachments - uses FileAttachmentComponent
        AddToMap(map, "knowledge/files/", typeof(FileAttachmentComponent));
        // Skills
        AddToMap(map, "skills/", typeof(SkillDefinition));
        // External triggers
        AddToMap(map, "trigger/", typeof(ExternalTriggerConfiguration));
        // Test cases
        AddToMap(map, "testcases/", typeof(TestDefinitionBase));
        // Custom metric definitions
        AddToMap(map, "custommetrics/", typeof(CustomMetricDefinition));
        // Agent skills
        AddToMap(map, "agentskills/", typeof(AgentSkillMetadata));
        // Environment variables
        AddToMap(map, "environmentvariables/", typeof(EnvironmentVariableDefinition));

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
