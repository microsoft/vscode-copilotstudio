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
    internal const string AgentInfix = ".agent.";
    internal const string FileAttachmentInfix = ".file.";
    internal const string AgentsFolder = "agents/";

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
    /// Optional legacy function used to determine whether this override applies for a given file or schema.
    /// It receives the path without extension and schema name.
    /// </param>
    /// <param name="ContextPredicate">
    /// Optional graph-aware function used to determine whether this override applies for a component.
    /// </param>
    /// <param name="FolderResolver">
    /// Optional graph-aware folder resolver. When present, it replaces <paramref name="Folder"/>.
    /// </param>
    internal readonly record struct RuleOverride(
        string Infix,
        string Folder,
        string[]? DotInfixBlocklist = null,
        Func<string?, string?, bool>? Predicate = null,
        Func<RuleContext, bool>? ContextPredicate = null,
        Func<RuleContext, string?>? FolderResolver = null);

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
    /// <param name="PreserveBotPrefixedFiles">
    /// If true, bot-prefixed display-name filenames (e.g. <c>{botName}.MyFile_id</c>)
    /// are preserved as-is instead of being normalized/expanded with the infix. Used by
    /// the CLI knowledge/file three-layer rules (TDD D21).
    /// </param>
    internal readonly record struct Rule(string Infix, string Folder, bool DotPassthrough = false, string[]? DotInfixBlocklist = null, RuleOverride[]? Overrides = null, bool PreserveBotPrefixedFiles = false);

    /// <summary>
    /// Graph-aware context available to projection rules.
    /// </summary>
    internal readonly record struct RuleContext(
        Type ElementType,
        string? SchemaName,
        string? BotName,
        string? SubAgentFolder,
        string? PathWithoutExtension,
        AuthoringShape Shape,
        BotComponentBase? Component,
        BotDefinition? Definition)
    {
        internal BotComponentBase? ParentComponent
        {
            get
            {
                if (Definition == null || Component == null || !Component.ParentBotComponentId.HasValue)
                {
                    return null;
                }

                return Definition.TryGetBotComponentById(Component.ParentBotComponentId.Value, out var parent)
                    ? parent
                    : null;
            }
        }

        internal BotElement? ParentRootElement => (ParentComponent as DialogComponent)?.RootElement;
    }

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
                            ".topic.",
                            AgentsFolder,
                            new[] { "topic" },
                            (path, schema) => FileNameHasTopicPrefix(path)
                                              || schema?.Contains(".topic.", StringComparison.OrdinalIgnoreCase) == true),
                        new RuleOverride(
                            ".InvokeConnectedAgentTaskAction.",
                            AgentsFolder,
                            new[] { "InvokeConnectedAgentTaskAction" },
                            (path, schema) => (path?.StartsWith(AgentsFolder, StringComparison.OrdinalIgnoreCase) == true
                                               && path?.Contains("/actions/", StringComparison.OrdinalIgnoreCase) != true
                                               && !FileNameHasTopicPrefix(path))
                                              || schema?.Contains(".InvokeConnectedAgentTaskAction.", StringComparison.OrdinalIgnoreCase) == true)
                    })
            },

            // Agent dialogs
            {
                typeof(AgentDialog),
                new Rule(AgentInfix, AgentsFolder, false)
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
                new Rule(FileAttachmentInfix, "knowledge/files/", true, new[] { "file" })
            },
            {
                typeof(FileAttachmentComponent),
                new Rule(FileAttachmentInfix, "knowledge/files/", true, new[] { "file" })
            },

            // Variables
            {
                typeof(VariableBase),
                new Rule(".globalvariable.", "variables/", false)
            },
            {
                typeof(GlobalVariableComponent),
                new Rule(".globalvariable.", "variables/", false)
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
            }
            // https://github.com/microsoft/vscode-copilotstudio/issues/244
            //{ 
            //    typeof(AgentSkillComponent), 
            //    new Rule(".agentskill.", "agentskills/", true, new[] { "agentskill" }) 
            //},
        }.ToFrozenDictionary();

    /// <summary>
    /// CLI-agent (<see cref="AuthoringShape.CliCopilot"/>) projection overrides,
    /// consulted before the shared classic <see cref="Rules"/> when projecting a CLI
    /// agent (TDD D20). Classic and Unknown shapes never consult this map, so classic
    /// projection stays byte-identical.
    /// </summary>
    /// <remarks>
    /// Recovered from PR #265 (the CLI three-layer routes, TDD D21), but kept in this
    /// shape-gated map instead of the shared classic <see cref="Rules"/>. Connected
    /// agents route to <c>capabilities/tools/</c> per D10 (PR #265 used
    /// <c>capabilities/agents/</c>); knowledge/files route to <c>capabilities/knowledge/</c>
    /// and <c>capabilities/knowledge/files/</c> (D21, diverging from PR #265's
    /// <c>knowledge/</c>). Connection references are a separate Sync-overlay mechanism
    /// (<see cref="ConnectionReference"/> is not a <c>BotComponentBase</c>), handled by
    /// CliAgentConnectionsWriter at <c>infrastructure/connections/</c> - not projected
    /// here (D27).
    /// </remarks>
    internal static readonly FrozenDictionary<Type, Rule> CliRules = new Dictionary<Type, Rule>
        {
            // Behaviors (CLI inline skills) -> behaviors/
            {
                typeof(InlineAgentSkill),
                new Rule(".skill.", "behaviors/", true, new[] { "skill" })
            },

            // Tools -> capabilities/tools/
            {
                typeof(ConnectorTool),
                new Rule(".tool.", "capabilities/tools/", true, new[] { "tool" })
            },
            {
                typeof(WorkflowTool),
                new Rule(".tool.", "capabilities/tools/", true, new[] { "tool" })
            },
            {
                typeof(McpTool),
                new Rule(".tool.", "capabilities/tools/", true, new[] { "tool" })
            },
            // D10: connected agents route to capabilities/tools/, NOT capabilities/agents/.
            {
                typeof(ConnectedAgentTool),
                new Rule(".tool.connected-agent.", "capabilities/tools/", true, new[] { "tool" })
            },

            // Knowledge (shared type) -> capabilities/knowledge/ (D21). PreserveBotPrefixedFiles
            // keeps bot-prefixed display-name knowledge files (e.g. "{bot}.MyBook_id") as-is.
            {
                typeof(KnowledgeSource),
                new Rule(".knowledge.", "capabilities/knowledge/", true, new[] { "knowledge", "topic", "action" }, PreserveBotPrefixedFiles: true)
            },
            {
                typeof(KnowledgeSourceConfiguration),
                new Rule(".knowledge.", "capabilities/knowledge/", true, new[] { "knowledge", "topic", "action" }, PreserveBotPrefixedFiles: true)
            },
            {
                typeof(KnowledgeSourceComponent),
                new Rule(".knowledge.", "capabilities/knowledge/", true, new[] { "knowledge", "topic", "action" }, PreserveBotPrefixedFiles: true)
            },

            // File attachments (knowledge content) -> capabilities/knowledge/files/ (D21).
            // The metadata leaf is the schema-derived {leaf}.mcs.yml; the content file is
            // written to the SAME directory (download uses ParentDirectoryName), so moving
            // this rule relocates metadata + content together.
            {
                typeof(FileAttachmentComponentMetadata),
                new Rule(FileAttachmentInfix, "capabilities/knowledge/files/", true, new[] { "file" }, CreateCliFileAttachmentOverrides(), PreserveBotPrefixedFiles: true)
            },
            {
                typeof(FileAttachmentComponent),
                new Rule(FileAttachmentInfix, "capabilities/knowledge/files/", true, new[] { "file" }, CreateCliFileAttachmentOverrides(), PreserveBotPrefixedFiles: true)
            },
        }.ToFrozenDictionary();

    /// <summary>
    /// Top-level CLI component-body folders (TDD D30 allowlist), DERIVED from
    /// <see cref="CliRules"/> so the sync new-file scan and old-layout detection cannot
    /// drift from the projection: adding a CLI rule extends the writer, the resolver, and
    /// this scan together from one source of truth. A folder strictly nested under another
    /// in the set (e.g. <c>capabilities/knowledge/files/</c>, whose file-attachment
    /// content/metadata is discovered via the dedicated knowledge-file path, not the
    /// component scan) is excluded, since the scan matches direct children only. No
    /// trailing slash, to match the scan-site folder usage.
    /// </summary>
    internal static readonly string[] CliComponentBodyFolders = BuildCliComponentBodyFolders();

    private static string[] BuildCliComponentBodyFolders()
    {
        var all = CliRules.Values
            .Select(r => r.Folder)
            .Where(f => !string.IsNullOrEmpty(f))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return all
            .Where(f => !all.Any(other =>
                !string.Equals(other, f, StringComparison.Ordinal)
                && f.StartsWith(other, StringComparison.Ordinal)))
            .Select(PathHelper.ToInternalCanonicalFolderPath)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Gets the rule infix for an element type, if one exists.
    /// </summary>
    internal static string? GetRuleInfixForElementType(Type elementType)
        => GetRuleInfixForElementType(elementType, AuthoringShape.Classic);

    /// <summary>
    /// Shape-aware rule infix lookup (CLI agents consult <see cref="CliRules"/>).
    /// </summary>
    internal static string? GetRuleInfixForElementType(Type elementType, AuthoringShape shape)
    {
        return TryGetRuleForElementType(elementType, null, null, out var rule, shape) ? rule.Infix : null;
    }

    /// <summary>
    /// Gets the rule folder for an element type, if one exists.
    /// </summary>
    internal static string? GetRuleFolderForElementType(Type elementType)
        => GetRuleFolderForElementType(elementType, AuthoringShape.Classic);

    /// <summary>
    /// Shape-aware rule folder lookup (CLI agents consult <see cref="CliRules"/>).
    /// </summary>
    internal static string? GetRuleFolderForElementType(Type elementType, AuthoringShape shape)
    {
        if (!TryGetRuleForElementType(elementType, null, null, out var rule, shape))
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
            ".globalvariable.",
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the schema name for a file, applying legacy special cases.
    /// </summary>
    /// <param name="pathWithoutExtension">File path without extension.</param>
    /// <param name="botName">Bot entity schema name prefix.</param>
    /// <param name="elementType">Known element type for the file.</param>
    /// <returns>Full schema name (e.g., "botName.topic.MyTopic").</returns>
    /// <remarks>
    /// This exact 3-parameter signature is preserved (some callers and tests resolve
    /// it by reflection); the shape-aware variant is a separate overload.
    /// </remarks>
    internal static string? GetSchemaName(string pathWithoutExtension, string? botName, Type elementType)
    {
        return GetSchemaName(pathWithoutExtension, botName, elementType, AuthoringShape.Classic);
    }

    /// <summary>
    /// Shape-aware schema name derivation. CLI agents consult the CLI projection rules.
    /// </summary>
    internal static string? GetSchemaName(string pathWithoutExtension, string? botName, Type elementType, AuthoringShape shape)
    {
        return GetSchemaNameResult(pathWithoutExtension, botName, elementType, shape).SchemaName;
    }

    internal static SchemaNameResult GetSchemaNameResult(string pathWithoutExtension, string? botName, Type elementType, AuthoringShape shape = AuthoringShape.Classic)
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
        if (!TryGetRuleForElementType(elementType, null, normalized, out var rule, shape))
        {
            // No rule found - return null to let caller fall back
            return new SchemaNameResult(null, PreserveQualifiedSchemaName: false);
        }

        if (IsAgentsTopicRule(rule) && fileName.StartsWith("topic.", StringComparison.OrdinalIgnoreCase))
        {
            var name = fileName.Substring("topic.".Length);
            return new SchemaNameResult($"{botName}.topic.{name}", PreserveQualifiedSchemaName: false);
        }

        // Dot handling: follow legacy rules for dotted filenames
        if (fileName.Contains('.'))
        {
            // CLI knowledge/file rules keep bot-prefixed display-name files as-is.
            if (rule.PreserveBotPrefixedFiles && StartsWithBotPrefix(fileName, botName))
            {
                return new SchemaNameResult(fileName, PreserveQualifiedSchemaName: true);
            }

            if (ShouldExpandDottedName(rule, fileName))
            {
                return new SchemaNameResult(Expand(rule.Infix, fileName, botName), PreserveQualifiedSchemaName: false);
            }

            return new SchemaNameResult(fileName, PreserveQualifiedSchemaName: true);
        }

        // CLI knowledge/file rules also preserve underscore-bearing display-name files
        // that are not bot-prefixed (no dot, no infix to expand against).
        if (rule.PreserveBotPrefixedFiles
            && fileName.IndexOf('_') > 0
            && (string.IsNullOrEmpty(botName)
                || !fileName.StartsWith(botName + "_", StringComparison.OrdinalIgnoreCase)))
        {
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
    /// <returns>Always returns null. Use the path-aware <see cref="GetFilePath(Type, string, string?, string?, string?, AuthoringShape, BotComponentBase?, BotDefinition?)"/> overload instead.</returns>
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

    internal static string? GetFilePath(
        Type elementType,
        string schemaName,
        string? botName,
        string? subAgentFolder,
        string? pathWithoutExtension,
        AuthoringShape shape = AuthoringShape.Classic,
        BotComponentBase? component = null,
        BotDefinition? definition = null)
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
            var agentName = DeriveShortName(schemaName, AgentInfix, botName);
            return $"{prefix}{AgentsFolder}{agentName}/agent.mcs.yml";
        }

        // Look up rule
        var ruleContext = new RuleContext(elementType, schemaName, botName, subAgentFolder, pathWithoutExtension, shape, component, definition);
        if (!TryGetRuleForElementType(elementType, schemaName, pathWithoutExtension, out var rule, shape, ruleContext))
        {
            return null;
        }

        // Translations: AdaptiveDialog schema names with locale suffix should stay in translations/
        if (typeof(AdaptiveDialog).IsAssignableFrom(elementType) && IsTranslationSchemaName(schemaName))
        {
            rule = new Rule(rule.Infix, "translations/", rule.DotPassthrough);
        }

        var allowPreserve = !(subAgentFolder != null
            && (typeof(VariableBase).IsAssignableFrom(elementType) || typeof(GlobalVariableComponent).IsAssignableFrom(elementType)));

        var preserveQualifiedSchemaName = allowPreserve
            && (IsAlreadyQualifiedPath(rule, pathWithoutExtension)
                || (rule.PreserveBotPrefixedFiles && IsBotPrefixedFile(pathWithoutExtension, botName)));
        var shortName = DeriveShortName(schemaName, rule.Infix, botName, allowPreserve, preserveQualifiedSchemaName);

        if (IsAgentsTopicRule(rule) && !preserveQualifiedSchemaName)
        {
            shortName = $"topic.{shortName}";
        }

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
            return (AgentsFolder, AgentInfix);
        }

        return DefaultDialogProjection;
    }

    internal static string GetDialogInfixForFolder(string folderPath)
    {
        var normalized = PathHelper.ToInternalCanonicalFolderPath(folderPath);

        if (normalized.EndsWith("topics", StringComparison.OrdinalIgnoreCase))
        {
            return ".topic.";
        }

        if (normalized.EndsWith("actions", StringComparison.OrdinalIgnoreCase))
        {
            return ".action.";
        }

        if (normalized.IndexOf(AgentsFolder, StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return AgentInfix;
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

        var suffix = schemaName.Substring(lastDot + 1);
        return IsLocaleSuffix(suffix);
    }

    #region Private Helpers

    internal static bool TryGetRuleForElementType(
        Type elementType,
        string? schemaName,
        string? path,
        out Rule rule,
        AuthoringShape shape = AuthoringShape.Classic,
        RuleContext? context = null)
    {
        // CLI agents consult the CLI-specific overrides first; any type not present
        // there falls back to the shared classic Rules. Classic and Unknown shapes
        // never consult CliRules, so classic projection is byte-identical (TDD D20).
        if (shape == AuthoringShape.CliCopilot
            && TryGetRuleFromMap(CliRules, elementType, schemaName, path, out rule, context))
        {
            return true;
        }

        return TryGetRuleFromMap(Rules, elementType, schemaName, path, out rule, context);
    }

    private static bool TryGetRuleFromMap(FrozenDictionary<Type, Rule> map, Type elementType, string? schemaName, string? path, out Rule rule, RuleContext? context)
    {
        if (map.TryGetValue(elementType, out var exactRule))
        {
            rule = ResolveRule(exactRule, path, schemaName, context);
            return true;
        }

        // Check assignability for derived types, preferring more specific types
        // Track the most specific match (deepest in inheritance hierarchy)
        Type? bestMatch = null;
        Rule bestRule = default;

        foreach (var kvp in map)
        {
            if (kvp.Key.IsAssignableFrom(elementType))
            {
                // If this is the first match, or if this type is more specific than current best
                if (bestMatch == null || bestMatch.IsAssignableFrom(kvp.Key))
                {
                    bestMatch = kvp.Key;
                    bestRule = ResolveRule(kvp.Value, path, schemaName, context);
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

    private static Rule ResolveRule(Rule rule, string? path, string? schemaName, RuleContext? context)
    {
        path = path?.Replace('\\', '/');

        var overrides = rule.Overrides;
        if (overrides != null)
        {
            foreach (var ruleOverride in overrides)
            {
                var pathMatches = ruleOverride.Predicate == null || ruleOverride.Predicate(path, schemaName);
                var contextMatches = ruleOverride.ContextPredicate == null
                    || (context.HasValue && ruleOverride.ContextPredicate(context.Value));
                if (pathMatches && contextMatches)
                {
                    var folder = ruleOverride.Folder;
                    if (ruleOverride.FolderResolver != null && context.HasValue)
                    {
                        folder = ruleOverride.FolderResolver(context.Value) ?? folder;
                    }

                    return new Rule(
                        ruleOverride.Infix,
                        folder,
                        rule.DotPassthrough,
                        ruleOverride.DotInfixBlocklist ?? rule.DotInfixBlocklist,
                        PreserveBotPrefixedFiles: rule.PreserveBotPrefixedFiles);
                }
            }
        }

        return new Rule(rule.Infix, rule.Folder, rule.DotPassthrough, rule.DotInfixBlocklist, PreserveBotPrefixedFiles: rule.PreserveBotPrefixedFiles);
    }

    private static RuleOverride[] CreateCliFileAttachmentOverrides() =>
        new[]
        {
            new RuleOverride(
                FileAttachmentInfix,
                "capabilities/knowledge/files/",
                new[] { "file" },
                ContextPredicate: context => context.ParentRootElement is InlineAgentSkill,
                FolderResolver: GetParentComponentFolder),
        };

    private static string? GetParentComponentFolder(RuleContext context)
    {
        var parent = context.ParentComponent;
        if (parent == null)
        {
            return null;
        }

        var parentElementType = parent is DialogComponent dialogComponent
            ? dialogComponent.Dialog?.GetType() ?? typeof(AdaptiveDialog)
            : parent.GetType();

        var parentPath = GetFilePath(
            parentElementType,
            parent.SchemaNameString ?? string.Empty,
            context.BotName,
            subAgentFolder: null,
            pathWithoutExtension: null,
            context.Shape,
            parent,
            context.Definition);
        if (parentPath == null)
        {
            return null;
        }

        return PathHelper.ToInternalCanonicalFolderPath(new AgentFilePath(parentPath).RemoveExtension().ToString()) + "/";
    }

    private static string? GetAgentDialogSchemaName(string pathWithoutExtension, string? botName)
    {
        // Path should be: agents/{agentName}/agent
        if (!pathWithoutExtension.StartsWith(AgentsFolder, StringComparison.OrdinalIgnoreCase))
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

        // Sanitize the sub-agent folder segment so the schema short-name is always a
        // valid, cross-platform-safe name. This mirrors the schema->folder direction
        // (LspProjectorService / LspComponentPathResolver), which already projects the
        // folder via SubAgentFolderNaming.FromDisplayName, keeping the round-trip stable
        // (FromDisplayName is idempotent for already-clean folder names).
        var rawAgentName = parts[1];
        var agentName = SubAgentFolderNaming.FromDisplayName(rawAgentName);
        if (agentName == null)
        {
            throw new InvalidOperationException(
                $"Sub-agent folder '{rawAgentName}' contains no characters usable in a schema name. " +
                "Rename the folder to a cross-platform-safe name (letters, digits, '_' or '-').");
        }

        return $"{botName}{AgentInfix}{agentName}";
    }

    /// <summary>
    /// True when a filename is a bot-prefixed display-name file: it starts with
    /// "{botName}." and the remainder has no further dots (e.g. "{bot}.MyFile_id").
    /// </summary>
    private static bool StartsWithBotPrefix(string fileName, string? botName)
    {
        if (string.IsNullOrEmpty(botName) || string.IsNullOrEmpty(fileName))
        {
            return false;
        }

        if (!fileName.StartsWith(botName + ".", StringComparison.Ordinal))
        {
            return false;
        }

        var rest = fileName.Substring(botName!.Length + 1);
        return rest.Length > 0 && rest.IndexOf('.') < 0;
    }

    private static bool IsBotPrefixedFile(string? pathWithoutExtension, string? botName)
    {
        if (string.IsNullOrEmpty(pathWithoutExtension) || string.IsNullOrEmpty(botName))
        {
            return false;
        }

        var fileName = System.IO.Path.GetFileName(pathWithoutExtension!.Replace('\\', '/'));
        return StartsWithBotPrefix(fileName, botName);
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

    private static bool IsAgentsTopicRule(Rule rule)
    {
        return string.Equals(rule.Infix, ".topic.", StringComparison.OrdinalIgnoreCase) && string.Equals(rule.Folder, AgentsFolder, StringComparison.OrdinalIgnoreCase);
    }

    private static bool FileNameHasTopicPrefix(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var fileName = System.IO.Path.GetFileName(path!.Replace('\\', '/'));
        return fileName.StartsWith("topic.", StringComparison.OrdinalIgnoreCase);
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

        // Remove bot prefix.
        // schemaName is guaranteed non-null by the early-return at the top of this
        // method, but netstandard2.0's BCL lacks the [NotNullWhen(false)] annotation
        // on string.IsNullOrEmpty that net10 has, so the compiler can't narrow it.
        // The ! operator is a compile-time-only annotation (no IL emitted), symmetric
        // across both TFMs.
        var withoutPrefix = !string.IsNullOrEmpty(botName)
            ? schemaName!.Substring(botName.Length)
            : schemaName!;

        // Find and remove the infix
        var infixIndex = withoutPrefix.IndexOf(infix, StringComparison.OrdinalIgnoreCase);
        if (infixIndex >= 0)
        {
            var shortName = withoutPrefix.Substring(infixIndex + infix.Length);

            // Reserved filenames should not be shortened.
            if (IsReservedShortName(shortName, infix))
            {
                // schemaName non-null by early-return; ! is compile-time only.
                return schemaName!;
            }

            return shortName;
        }

        // Infix not found - schema name doesn't follow expected pattern.
        // Return original schemaName unchanged (e.g., already-qualified names
        // that passed through GetSchemaName without expansion).
        // schemaName non-null by early-return; ! is compile-time only.
        return schemaName!;
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
        AddToMap(map, "topics/", typeof(AdaptiveDialog));
        AddToMap(map, "actions/", typeof(TaskDialog));
        AddToMap(map, "agents/", typeof(TaskDialog));
        AddToMap(map, "translations/", typeof(AdaptiveDialog));
        AddToMap(map, "variables/", typeof(VariableBase));
        AddToMap(map, "settings/", typeof(BotSettingsBase));
        AddToMap(map, "entities/", typeof(EntityWithAnnotatedSamples));        // Entities - uses EntityWithAnnotatedSamples, not Entity
        AddToMap(map, "knowledge/", typeof(KnowledgeSource));                  // Knowledge - uses KnowledgeSource, not KnowledgeSourceConfiguration
        AddToMap(map, "knowledge/files/", typeof(FileAttachmentComponent));    // File attachments - uses FileAttachmentComponent
        AddToMap(map, "skills/", typeof(SkillDefinition));

        // CLI three-layer folders are disjoint from classic, so the combined
        // read-side map stays unambiguous. Connected agents -> capabilities/tools/
        AddToMap(map, "behaviors/", typeof(AgentSkillBase));
        AddToMap(map, "behaviors/", typeof(InlineAgentSkill));
        AddToMap(map, "capabilities/tools/", typeof(AgentToolBase));
        AddToMap(map, "capabilities/tools/", typeof(ConnectorTool));
        AddToMap(map, "capabilities/tools/", typeof(WorkflowTool));
        AddToMap(map, "capabilities/tools/", typeof(McpTool));
        AddToMap(map, "capabilities/tools/", typeof(ConnectedAgentTool));

        // CLI shared types: knowledge + file attachments
        AddToMap(map, "capabilities/knowledge/", typeof(KnowledgeSource));
        AddToMap(map, "capabilities/knowledge/files/", typeof(FileAttachmentComponent));

        AddToMap(map, "trigger/", typeof(ExternalTriggerConfiguration)); // External triggers
        AddToMap(map, "testcases/", typeof(TestDefinitionBase));         // Test cases
        AddToMap(map, "custommetrics/", typeof(CustomMetricDefinition)); // Custom metric definitions
        // AddToMap(map, "agentskills/", typeof(AgentSkillMetadata));     // not supported (oldstyle) Agent skills - https://github.com/microsoft/vscode-copilotstudio/issues/244
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
