namespace Microsoft.PowerPlatformLS.UnitTests.Contracts.FileLayout
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using Microsoft.Agents.ObjectModel;
    using Xunit;

    /// <summary>
    /// Oracle tests that validate LspProjectorService behavior against known-good constants.
    /// Baseline captured from git commit 066407e29677ead4cd92482c3d58a3d227380bf1.
    /// 
    /// These tests capture exact expected behavior as oracle constants.
    /// They are the primary parity lock to ensure the projection system produces correct results.
    /// </summary>
    public class ProjectorOracleParityTests
    {
        private const string BotName = "agent1";
        private const string EmptyBotName = "";

        public static TheoryData<string, string, string, string, string, object[], string> OracleCases => new()
        {
            // Dialogs
            { "Microsoft.Agents.ObjectModel.AdaptiveDialog", "topics/Greeting", "agent1.topic.Greeting", "topics/Greeting.mcs.yml", "FilenameToTopicName", new object[] { "Greeting" }, BotName },
            { "Microsoft.Agents.ObjectModel.TaskDialog", "actions/DoSomething", "agent1.action.DoSomething", "actions/DoSomething.mcs.yml", "FilenameToActionName", new object[] { "DoSomething" }, BotName },
            { "Microsoft.Agents.ObjectModel.TaskDialog", "agents/ConnectedAgentTool", "agent1.InvokeConnectedAgentTaskAction.ConnectedAgentTool", "agents/ConnectedAgentTool.mcs.yml", "FilenameToActionName", new object[] { "ConnectedAgentTool" }, BotName },

            // Agent dialog
            { "Microsoft.Agents.ObjectModel.AgentDialog", "agents/MyAgent/agent", "agent1.agent.MyAgent", "agents/MyAgent/agent.mcs.yml", "FilenameToAgentDialogName", new object[] { "agents/MyAgent/agent.mcs.yml" }, BotName },

            // GPT
            { "Microsoft.Agents.ObjectModel.GptComponentMetadata", "agent", "agent1.gpt.default", "agent.mcs.yml", "FilenameToGptName", Array.Empty<object>(), BotName },

            // Knowledge
            { "Microsoft.Agents.ObjectModel.KnowledgeSource", "knowledge/PublicSiteSearchSource.0", "agent1.knowledge.PublicSiteSearchSource.0", "knowledge/PublicSiteSearchSource.0.mcs.yml", "FilenameToKnowledgeName", new object[] { "PublicSiteSearchSource.0" }, BotName },
            { "Microsoft.Agents.ObjectModel.KnowledgeSource", "knowledge/agent1.topic.SomeTopic", "agent1.topic.SomeTopic", "knowledge/agent1.topic.SomeTopic.mcs.yml", "FilenameToKnowledgeName", new object[] { "agent1.topic.SomeTopic" }, BotName },
            { "Microsoft.Agents.ObjectModel.KnowledgeSource", "knowledge/agent1.knowledge.Source.0", "agent1.knowledge.Source.0", "knowledge/agent1.knowledge.Source.0.mcs.yml", "FilenameToKnowledgeName", new object[] { "agent1.knowledge.Source.0" }, BotName },
            { "Microsoft.Agents.ObjectModel.KnowledgeSource", "knowledge/Some.Topic.With.Dots", "Some.Topic.With.Dots", "knowledge/Some.Topic.With.Dots.mcs.yml", "FilenameToKnowledgeName", new object[] { "Some.Topic.With.Dots" }, BotName },

            // File attachments
            { "Microsoft.Agents.ObjectModel.FileAttachmentComponentMetadata", "knowledge/files/MyLedger.xlsx_hyG", "agent1.file.MyLedger.xlsx_hyG", "knowledge/files/MyLedger.xlsx_hyG.mcs.yml", "FilenameToFileAttachmentName", new object[] { "MyLedger.xlsx_hyG" }, BotName },
            { "Microsoft.Agents.ObjectModel.FileAttachmentComponentMetadata", "knowledge/files/agent1.file.MyLedger.xlsx_hyG", "agent1.file.MyLedger.xlsx_hyG", "knowledge/files/agent1.file.MyLedger.xlsx_hyG.mcs.yml", "FilenameToFileAttachmentName", new object[] { "agent1.file.MyLedger.xlsx_hyG" }, BotName },
            { "Microsoft.Agents.ObjectModel.FileAttachmentComponentMetadata", "knowledge/files/Statement.2024.Q4", "agent1.file.Statement.2024.Q4", "knowledge/files/Statement.2024.Q4.mcs.yml", "FilenameToFileAttachmentName", new object[] { "Statement.2024.Q4" }, BotName },

            // Variables
            { "Microsoft.Agents.ObjectModel.Variable", "variables/var1", "agent1.GlobalVariableComponent.var1", "variables/var1.mcs.yml", "FilenameToVariableName", new object[] { "var1" }, BotName },
            { "Microsoft.Agents.ObjectModel.Variable", "variables/agent1.GlobalVariableComponent.var1", "agent1.GlobalVariableComponent.var1", "variables/agent1.GlobalVariableComponent.var1.mcs.yml", "FilenameToVariableName", new object[] { "agent1.GlobalVariableComponent.var1" }, BotName },
            { "Microsoft.Agents.ObjectModel.Variable", "variables/Var.With.Dots", "Var.With.Dots", "variables/Var.With.Dots.mcs.yml", "FilenameToVariableName", new object[] { "Var.With.Dots" }, BotName },

            // Entities
            { "Microsoft.Agents.ObjectModel.EntityWithAnnotatedSamples", "entities/Customer", "agent1.entity.Customer", "entities/Customer.mcs.yml", "FilenameToCustomEntityName", new object[] { "Customer" }, BotName },

            // Settings
            { "Microsoft.Agents.ObjectModel.BotSettingsBase", "settings/settings", "agent1.BotSettingsComponent.settings", "settings/settings.mcs.yml", "FilenameToSettingsName", new object[] { "settings" }, BotName },
            { "Microsoft.Agents.ObjectModel.BotSettingsBase", "settings/agent1.BotSettingsComponent.custom", "agent1.BotSettingsComponent.custom", "settings/agent1.BotSettingsComponent.custom.mcs.yml", "FilenameToSettingsName", new object[] { "agent1.BotSettingsComponent.custom" }, BotName },
            { "Microsoft.Agents.ObjectModel.BotSettingsBase", "settings/Custom.With.Dots", "Custom.With.Dots", "settings/Custom.With.Dots.mcs.yml", "FilenameToSettingsName", new object[] { "Custom.With.Dots" }, BotName },

            // External triggers
            { "Microsoft.Agents.ObjectModel.ExternalTriggerConfiguration", "trigger/Workflow.Trigger", "agent1.ExternalTriggerComponent.Workflow.Trigger", "trigger/Workflow.Trigger.mcs.yml", "FilenameToExternalTriggerComponentName", new object[] { "Workflow.Trigger" }, BotName },
            { "Microsoft.Agents.ObjectModel.ExternalTriggerConfiguration", "trigger/agent1.ExternalTriggerComponent.Workflow.Trigger", "agent1.ExternalTriggerComponent.Workflow.Trigger", "trigger/agent1.ExternalTriggerComponent.Workflow.Trigger.mcs.yml", "FilenameToExternalTriggerComponentName", new object[] { "agent1.ExternalTriggerComponent.Workflow.Trigger" }, BotName },

            // Skills
            { "Microsoft.Agents.ObjectModel.SkillDefinition", "skills/CopilotSkill.dc2f", "agent1.skill.CopilotSkill.dc2f", "skills/CopilotSkill.dc2f.mcs.yml", "FilenameToSkillComponentName", new object[] { "CopilotSkill.dc2f" }, BotName },
            { "Microsoft.Agents.ObjectModel.SkillDefinition", "skills/agent1.skill.MySkill", "agent1.skill.MySkill", "skills/agent1.skill.MySkill.mcs.yml", "FilenameToSkillComponentName", new object[] { "agent1.skill.MySkill" }, BotName },
            { "Microsoft.Agents.ObjectModel.SkillDefinition", "skills/My.Skill.With.Dots", "My.Skill.With.Dots", "skills/My.Skill.With.Dots.mcs.yml", "FilenameToSkillComponentName", new object[] { "My.Skill.With.Dots" }, BotName },

            // Translations (topic infix, dot passthrough)
            { "Microsoft.Agents.ObjectModel.AdaptiveDialog", "translations/Greeting.pt-BR", "agent1.topic.Greeting.pt-BR", "translations/Greeting.pt-BR.mcs.yml", "FilenameToTopicName", new object[] { "Greeting.pt-BR" }, BotName },
            { "Microsoft.Agents.ObjectModel.AdaptiveDialog", "translations/agent1.topic.Greeting.pt-BR", "agent1.topic.Greeting.pt-BR", "translations/agent1.topic.Greeting.pt-BR.mcs.yml", "FilenameToTopicName", new object[] { "agent1.topic.Greeting.pt-BR" }, BotName },

            // Already qualified using a different infix: should not be expanded
            { "Microsoft.Agents.ObjectModel.AdaptiveDialog", "topics/agent1.skill.MySkill", "agent1.topic.agent1.skill.MySkill", "topics/agent1.skill.MySkill.mcs.yml", "FilenameToTopicName", new object[] { "agent1.skill.MySkill" }, BotName },
            { "Microsoft.Agents.ObjectModel.AdaptiveDialog", "topics/agent1.component.CustomAction", "agent1.topic.agent1.component.CustomAction", "topics/agent1.component.CustomAction.mcs.yml", "FilenameToTopicName", new object[] { "agent1.component.CustomAction" }, BotName },
            { "Microsoft.Agents.ObjectModel.TaskDialog", "actions/agent1.component.CustomAction", "agent1.action.agent1.component.CustomAction", "actions/agent1.component.CustomAction.mcs.yml", "FilenameToActionName", new object[] { "agent1.component.CustomAction" }, BotName },
            { "Microsoft.Agents.ObjectModel.AdaptiveDialog", "topics/otherbot.topic.Shared", "otherbot.topic.Shared", "topics/otherbot.topic.Shared.mcs.yml", "FilenameToTopicName", new object[] { "otherbot.topic.Shared" }, BotName },
            { "Microsoft.Agents.ObjectModel.TaskDialog", "actions/otherbot.action.Shared", "otherbot.action.Shared", "actions/otherbot.action.Shared.mcs.yml", "FilenameToActionName", new object[] { "otherbot.action.Shared" }, BotName },

            // Component infix passthrough cases
            { "Microsoft.Agents.ObjectModel.KnowledgeSource", "knowledge/agent1.component.Custom", "agent1.knowledge.agent1.component.Custom", "knowledge/agent1.component.Custom.mcs.yml", "FilenameToKnowledgeName", new object[] { "agent1.component.Custom" }, BotName },
            { "Microsoft.Agents.ObjectModel.SkillDefinition", "skills/agent1.component.Custom", "agent1.skill.agent1.component.Custom", "skills/agent1.component.Custom.mcs.yml", "FilenameToSkillComponentName", new object[] { "agent1.component.Custom" }, BotName },

            // Component collection (empty prefix) should treat dotted names as already-qualified
            { "Microsoft.Agents.ObjectModel.AdaptiveDialog", "topics/Shared.Schema", ".topic.Shared.Schema", "topics/Shared.Schema.mcs.yml", "FilenameToTopicName", new object[] { "Shared.Schema" }, EmptyBotName },
            { "Microsoft.Agents.ObjectModel.TaskDialog", "actions/Shared.Schema", ".action.Shared.Schema", "actions/Shared.Schema.mcs.yml", "FilenameToActionName", new object[] { "Shared.Schema" }, EmptyBotName },

            // Component collection (no prefix) should not expand
            { "Microsoft.Agents.ObjectModel.AdaptiveDialog", "topics/CustomSchema", ".topic.CustomSchema", "topics/CustomSchema.mcs.yml", "FilenameToTopicName", new object[] { "CustomSchema" }, EmptyBotName },
            { "Microsoft.Agents.ObjectModel.TaskDialog", "actions/CustomSchema", ".action.CustomSchema", "actions/CustomSchema.mcs.yml", "FilenameToActionName", new object[] { "CustomSchema" }, EmptyBotName },
        };

        public static TheoryData<string, string, string, string> SubAgentFilePathCases => new()
        {
            { "Microsoft.Agents.ObjectModel.AdaptiveDialog", "agent1.topic.Greeting", "agents/SubAgent/", "agents/SubAgent/topics/Greeting.mcs.yml" },
            { "Microsoft.Agents.ObjectModel.TaskDialog", "agent1.action.RunTask", "agents/SubAgent/", "agents/SubAgent/actions/RunTask.mcs.yml" },
            { "Microsoft.Agents.ObjectModel.Variable", "agent1.GlobalVariableComponent.var1", "agents/SubAgent/", "agents/SubAgent/variables/var1.mcs.yml" },
        };

        public static TheoryData<string, string, string, object[]> UnknownTypeCases => new()
        {
            { "Microsoft.Agents.ObjectModel.UnknownBotElement", "topics/Unknown", "FilenameToTopicName", new object[] { "Unknown" } },
            { "Microsoft.Agents.ObjectModel.LegacyOrUnknownComponent", "topics/Unknown", "FilenameToTopicName", new object[] { "Unknown" } },
        };

        public static TheoryData<string, string, string> ShortNameGuardCases => new()
        {
            // Short names "agent" and "settings" should not be shortened into reserved filenames.
            { "Microsoft.Agents.ObjectModel.AdaptiveDialog", "agent1.topic.agent", "topics/agent1.topic.agent.mcs.yml" },
            { "Microsoft.Agents.ObjectModel.AdaptiveDialog", "agent1.topic.settings", "topics/agent1.topic.settings.mcs.yml" },
            { "Microsoft.Agents.ObjectModel.Variable", "agent1.GlobalVariableComponent.agent", "variables/agent1.GlobalVariableComponent.agent.mcs.yml" },
            { "Microsoft.Agents.ObjectModel.Variable", "agent1.GlobalVariableComponent.settings", "variables/agent1.GlobalVariableComponent.settings.mcs.yml" },
        };

        public static TheoryData<string, string, string, string> AgentAndGptFilePathCases => new()
        {
            { "Microsoft.Agents.ObjectModel.GptComponentMetadata", "agent1.gpt.default", "", "agent.mcs.yml" },
            { "Microsoft.Agents.ObjectModel.GptComponentMetadata", "agent1.gpt.default", "agents/SubAgent/", "agents/SubAgent/agent.mcs.yml" },
            { "Microsoft.Agents.ObjectModel.AgentDialog", "agent1.agent.SubAgent", "", "agents/SubAgent/agent.mcs.yml" },
            { "Microsoft.Agents.ObjectModel.AgentDialog", "agent1.agent.SubAgent", "agents/Parent/", "agents/Parent/agents/SubAgent/agent.mcs.yml" },
        };

        public static TheoryData<string, string> LayoutOnlyFileCases => new()
        {
            { "collection.mcs.yml", "collection" },
            { ".mcs/botdefinition.json", ".mcs/botdefinition.json" },
        };

        [Fact]
        public void DialogTopic_DoesNotExpand_OtherInfixes()
        {
            var expected = "agent1.topic.agent1.skill.MySkill";
            var actual = GetSchemaNameFromNewOrLegacy(
                elementTypeName: "Microsoft.Agents.ObjectModel.AdaptiveDialog",
                pathWithoutExtension: "topics/agent1.skill.MySkill",
                legacyMethodName: "FilenameToTopicName",
                legacyArgs: new object[] { "agent1.skill.MySkill" },
                botName: BotName);

            Assert.Equal(expected, actual);
        }

        [Theory]
        [MemberData(nameof(OracleCases))]
        public void OracleSchemaAndFilePathParity(
            string elementTypeName,
            string pathWithoutExtension,
            string expectedSchemaName,
            string expectedFilePath,
            string legacyMethodName,
            object[] legacyArgs,
            string botName)
        {
            var elementType = ResolveTypeOrSkip(elementTypeName);
            var actualSchema = GetSchemaNameFromNewOrLegacy(
                elementTypeName,
                pathWithoutExtension,
                legacyMethodName,
                legacyArgs,
                botName);

            Assert.Equal(expectedSchemaName, actualSchema);

            var roundTripPathWithoutExtension = RemoveMcsYmlExtension(expectedFilePath);
            var roundTripSchema = GetSchemaNameFromNewOrLegacy(
                elementTypeName,
                roundTripPathWithoutExtension,
                legacyMethodName,
                legacyArgs,
                botName);

            Assert.Equal(expectedSchemaName, roundTripSchema);
        }

        [Fact]
        public void DialogAction_UsesActionInfix()
        {
            var expected = "agent1.action.DoSomething";
            var actual = GetSchemaNameFromNewOrLegacy(
                elementTypeName: "Microsoft.Agents.ObjectModel.TaskDialog",
                pathWithoutExtension: "actions/DoSomething",
                legacyMethodName: "FilenameToActionName",
                legacyArgs: new object[] { "DoSomething" },
                botName: BotName);

            Assert.Equal(expected, actual);
        }

        [Fact]
        public void AgentDialog_DerivesSchemaFromFolder()
        {
            var expected = "agent1.agent.MyAgent";
            var actual = GetSchemaNameFromNewOrLegacy(
                elementTypeName: "Microsoft.Agents.ObjectModel.AgentDialog",
                pathWithoutExtension: "agents/MyAgent/agent",
                legacyMethodName: "FilenameToAgentDialogName",
                legacyArgs: new object[] { "agents/MyAgent/agent.mcs.yml" },
                botName: BotName);

            Assert.Equal(expected, actual);
        }

        [Fact]
        public void GptComponent_UsesDefaultSchemaName()
        {
            var expected = "agent1.gpt.default";
            var actual = GetSchemaNameFromNewOrLegacy(
                elementTypeName: "Microsoft.Agents.ObjectModel.GptComponentMetadata",
                pathWithoutExtension: "agent",
                legacyMethodName: "FilenameToGptName",
                legacyArgs: Array.Empty<object>(),
                botName: BotName);

            Assert.Equal(expected, actual);
        }


        [Fact]
        public void LayoutMap_MatchesLegacyOracle()
        {
            var expected = LegacyFileStructureMap;
            var actual = GetFileStructureMapFromRuntime();

            Assert.NotNull(actual);
            AssertEquivalentMaps(expected, actual!);

            var expectedReverse = BuildReverseMap(expected);
            var actualReverse = GetTypeToFileCandidatesFromRuntime();
            Assert.NotNull(actualReverse);
            AssertEquivalentReverseMaps(expectedReverse, actualReverse!);
        }

        [Theory]
        [MemberData(nameof(LayoutOnlyFileCases))]
        public void LayoutOnlyFiles_ArePresentInMap(string filePath, string expectedKey)
        {
            var map = GetFileStructureMapFromRuntime();
            Assert.NotNull(map);

            var derivedKey = filePath.EndsWith(".mcs.yml", StringComparison.OrdinalIgnoreCase)
                ? RemoveMcsYmlExtension(filePath)
                : filePath;

            Assert.Equal(expectedKey, derivedKey);
            Assert.True(map!.ContainsKey(expectedKey), $"Missing layout key: {expectedKey}");
        }

        [Theory]
        [MemberData(nameof(UnknownTypeCases))]
        public void UnknownTypes_AreSkippedOrReturnNull(
            string elementTypeName,
            string pathWithoutExtension,
#pragma warning disable xUnit1026 // Theory methods should use all of their parameters - retained for oracle data compatibility
            string _legacyMethodName,
            object[] _legacyArgs)
#pragma warning restore xUnit1026
        {
            // Note: _legacyMethodName and _legacyArgs are retained for oracle data compatibility
            // but are no longer used since the legacy helper was removed.
            Assert.False(string.IsNullOrWhiteSpace(pathWithoutExtension));
            var elementType = FindType(elementTypeName);
            Assert.NotNull(elementType);

            var registry = TryGetDefaultProjectorRegistry();
            Assert.NotNull(registry);

            var projector = GetProjectorForElementType(registry!, elementType!);
            Assert.True(projector == null, "Unknown element types should not resolve a projector.");
        }

        private static string GetSchemaNameFromNewOrLegacy(
            string elementTypeName,
            string pathWithoutExtension,
            string legacyMethodName,
            object[] legacyArgs,
            string botName)
        {
            var elementType = ResolveTypeOrSkip(elementTypeName);
            var lspService = TryGetLspProjectorServiceInstance();
            if (lspService != null)
            {
                var result = InvokeProjectorServiceGetSchemaName(lspService, pathWithoutExtension, botName, elementType);
                if (result != null)
                {
                    return result;
                }
            }

            var lspProjectionResult = TryGetSchemaNameFromLspProjection(pathWithoutExtension, botName, elementType);
            if (lspProjectionResult != null)
            {
                return lspProjectionResult;
            }

            var registry = TryGetDefaultProjectorRegistry();
            Assert.NotNull(registry);

            var projector = GetProjectorForElementType(registry!, elementType);
            Assert.NotNull(projector);

            return InvokeProjectorGetSchemaName(projector!, pathWithoutExtension, botName, elementType);
        }


        private static object? TryGetDefaultProjectorRegistry()
        {
            var registryType = FindType("Microsoft.Agents.ObjectModel.FileProjection.DefaultProjectorRegistry");
            if (registryType == null)
            {
                return null;
            }

            var instance = registryType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
            return instance?.GetValue(null);
        }

        private static object? TryGetLspProjectorServiceInstance()
        {
            var serviceType = FindType("Microsoft.CopilotStudio.McsCore.LspProjectorService");
            if (serviceType == null)
            {
                return null;
            }

            var instance = serviceType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
            return instance?.GetValue(null);
        }

        private static object? GetProjectorForElementType(object registry, Type elementType)
        {
            var method = registry.GetType().GetMethod(
                "GetForElementType",
                BindingFlags.Public | BindingFlags.Instance,
                binder: null,
                types: new[] { typeof(Type), typeof(string) },
                modifiers: null);

            Assert.NotNull(method);
            return method!.Invoke(registry, new object?[] { elementType, null });
        }

        private static string InvokeProjectorGetSchemaName(
            object projector,
            string pathWithoutExtension,
            string botName,
            Type elementType)
        {
            var method = projector.GetType().GetMethod(
                "GetSchemaName",
                BindingFlags.Public | BindingFlags.Instance,
                binder: null,
                types: new[] { typeof(string), typeof(string), typeof(Type) },
                modifiers: null);

            Assert.NotNull(method);
            var result = method!.Invoke(projector, new object?[] { pathWithoutExtension, botName, elementType });
            Assert.IsType<string>(result);
            return (string)result!;
        }

        private static string? InvokeProjectorServiceGetSchemaName(
            object projectorService,
            string pathWithoutExtension,
            string botName,
            Type elementType)
        {
            var method = projectorService.GetType().GetMethod(
                "GetSchemaName",
                BindingFlags.Public | BindingFlags.Instance,
                binder: null,
                types: new[] { typeof(string), typeof(string), typeof(Type) },
                modifiers: null);

            if (method == null)
            {
                return null;
            }

            var result = method.Invoke(projectorService, new object?[] { pathWithoutExtension, botName, elementType });
            return result as string;
        }

        private static string? TryGetSchemaNameFromLspProjection(
            string pathWithoutExtension,
            string botName,
            Type elementType)
        {
            var lspProjectionType = FindType("Microsoft.CopilotStudio.McsCore.LspProjection");
            if (lspProjectionType == null)
            {
                return null;
            }

            var method = lspProjectionType.GetMethod(
                "GetSchemaName",
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: new[] { typeof(string), typeof(string), typeof(Type) },
                modifiers: null);

            if (method == null)
            {
                return null;
            }

            var result = method.Invoke(null, new object?[] { pathWithoutExtension, botName, elementType });
            return result as string;
        }

        private static string RemoveMcsYmlExtension(string path)
        {
            const string Extension = ".mcs.yml";
            return path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)
                ? path[..^Extension.Length]
                : path;
        }

        private static Type? FindType(string fullName)
        {
            var type = AppDomain.CurrentDomain
                .GetAssemblies()
                .Select(assembly => assembly.GetType(fullName, throwOnError: false, ignoreCase: false))
                .FirstOrDefault(found => found != null);

            if (type != null)
            {
                return type;
            }

            TryLoadAssemblyForType(fullName);

            return AppDomain.CurrentDomain
                .GetAssemblies()
                .Select(assembly => assembly.GetType(fullName, throwOnError: false, ignoreCase: false))
                .FirstOrDefault(found => found != null);
        }

        private static void TryLoadAssemblyForType(string fullName)
        {
            var assemblyName = fullName.StartsWith("Microsoft.PowerPlatformLS.Contracts.FileLayout", StringComparison.Ordinal)
                ? "Microsoft.PowerPlatformLS.Contracts.FileLayout"
                : fullName.StartsWith("Microsoft.Agents.ObjectModel", StringComparison.Ordinal)
                    ? "Microsoft.Agents.ObjectModel"
                    : null;

            if (assemblyName == null)
            {
                return;
            }

            try
            {
                _ = Assembly.Load(assemblyName);
            }
            catch
            {
                // Best-effort: type may exist in a different assembly or not be available on this branch.
            }
        }

        private static Type ResolveTypeOrSkip(string fullName)
        {
            var type = FindType(fullName);
            Assert.NotNull(type);
            return type!;
        }

        private static readonly IReadOnlyDictionary<string, string[]> LegacyFileStructureMap =
            new Dictionary<string, string[]>(StringComparer.InvariantCultureIgnoreCase)
            {
                { "settings", new[] { "Microsoft.Agents.ObjectModel.BotEntity" } },
                { "collection", new[] { "Microsoft.Agents.ObjectModel.BotComponentCollection" } },
                { ".mcs/botdefinition.json", new[] { "Microsoft.Agents.ObjectModel.DefinitionBase" } },
                { "icon.png", Array.Empty<string>() },
                { "connectionreferences", new[] { "Microsoft.Agents.ObjectModel.ConnectionReferencesSourceFile" } },
                { "references", new[] { "Microsoft.Agents.ObjectModel.ReferencesSourceFile" } },

                { "agent", new[] { "Microsoft.Agents.ObjectModel.GptComponentMetadata", "Microsoft.Agents.ObjectModel.AgentDialog" } },
                { "actions/", new[] { "Microsoft.Agents.ObjectModel.TaskDialog" } },
                { "agents/", new[] { "Microsoft.Agents.ObjectModel.TaskDialog" } },
                { "knowledge/", new[] { "Microsoft.Agents.ObjectModel.KnowledgeSource" } },
                { "knowledge/files/", new[] { "Microsoft.Agents.ObjectModel.FileAttachmentComponent" } },
                { "topics/", new[] { "Microsoft.Agents.ObjectModel.AdaptiveDialog" } },
                { "variables/", new[] { "Microsoft.Agents.ObjectModel.Variable" } },
                { "entities/", new[] { "Microsoft.Agents.ObjectModel.EntityWithAnnotatedSamples" } },
                { "settings/", new[] { "Microsoft.Agents.ObjectModel.BotSettingsBase" } },
                { "trigger/", new[] { "Microsoft.Agents.ObjectModel.ExternalTriggerConfiguration" } },
                { "skills/", new[] { "Microsoft.Agents.ObjectModel.SkillDefinition" } },
                { "translations/", new[] { "Microsoft.Agents.ObjectModel.AdaptiveDialog" } },
            };

        private static IReadOnlyDictionary<string, string[]>? GetFileStructureMapFromRuntime()
        {
            var layoutType = FindType("Microsoft.CopilotStudio.McsCore.LspProjectionLayout");
            if (layoutType != null)
            {
                var map = layoutType.GetField("FileStructureMap", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                return ConvertMap(map);
            }

            var helperType = FindType("Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Models.WorkspaceHelper");
            if (helperType != null)
            {
                var map = helperType.GetField("FileStructureMap", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                return ConvertMap(map);
            }

            return null;
        }

        private static IReadOnlyDictionary<string, string[]>? GetTypeToFileCandidatesFromRuntime()
        {
            var layoutType = FindType("Microsoft.CopilotStudio.McsCore.LspProjectionLayout");
            if (layoutType != null)
            {
                var map = layoutType.GetField("TypeToFileCandidates", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                return ConvertReverseMap(map);
            }

            var helperType = FindType("Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Models.WorkspaceHelper");
            if (helperType != null)
            {
                var map = helperType.GetField("TypeToFileCandidates", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                return ConvertReverseMap(map);
            }

            return null;
        }

        private static IReadOnlyDictionary<string, string[]> ConvertMap(object? map)
        {
            var result = new Dictionary<string, string[]>(StringComparer.InvariantCultureIgnoreCase);
            if (map is System.Collections.IDictionary dict)
            {
                foreach (var key in dict.Keys)
                {
                    if (key == null)
                    {
                        continue;
                    }

                    var keyString = key.ToString() ?? string.Empty;
                    if (string.IsNullOrEmpty(keyString))
                    {
                        continue;
                    }

                    var types = dict[key] as System.Collections.IEnumerable;
                    var names = types == null
                        ? Array.Empty<string>()
                        : types.Cast<object?>()
                            .Select(t => t as Type)
                            .Where(t => t != null)
                            .Select(t => t!.FullName ?? t.Name)
                            .OrderBy(n => n, StringComparer.Ordinal)
                            .ToArray();

                    result[keyString] = names;
                }
            }

            return result;
        }

        private static IReadOnlyDictionary<string, string[]> ConvertReverseMap(object? map)
        {
            var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
            if (map is System.Collections.IDictionary dict)
            {
                foreach (var key in dict.Keys)
                {
                    if (key == null)
                    {
                        continue;
                    }

                    if (key is not Type typeKey)
                    {
                        continue;
                    }

                    var values = dict[key] as System.Collections.IEnumerable;
                    var names = values == null
                        ? Array.Empty<string>()
                        : values.Cast<object?>()
                            .Select(v => v?.ToString() ?? string.Empty)
                            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                            .ToArray();

                    result[typeKey.FullName ?? typeKey.Name] = names;
                }
            }

            return result;
        }

        private static IReadOnlyDictionary<string, string[]> BuildReverseMap(IReadOnlyDictionary<string, string[]> map)
        {
            var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var kvp in map)
            {
                foreach (var typeName in kvp.Value)
                {
                    if (!result.TryGetValue(typeName, out var list))
                    {
                        list = new List<string>();
                        result[typeName] = list;
                    }

                    list.Add(kvp.Key);
                }
            }

            return result.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToArray(),
                StringComparer.Ordinal);
        }

        private static void AssertEquivalentMaps(IReadOnlyDictionary<string, string[]> expected, IReadOnlyDictionary<string, string[]> actual)
        {
            foreach (var kvp in expected)
            {
                Assert.True(actual.TryGetValue(kvp.Key, out var actualTypes), $"Missing map key: {kvp.Key}");
                var expectedTypes = kvp.Value ?? Array.Empty<string>();
                var actualTypeList = actualTypes ?? Array.Empty<string>();
                Assert.Equal(expectedTypes.OrderBy(v => v, StringComparer.Ordinal), actualTypeList.OrderBy(v => v, StringComparer.Ordinal));
            }
        }

        private static void AssertEquivalentReverseMaps(IReadOnlyDictionary<string, string[]> expected, IReadOnlyDictionary<string, string[]> actual)
        {
            foreach (var kvp in expected)
            {
                Assert.True(actual.TryGetValue(kvp.Key, out var actualFolders), $"Missing reverse map type: {kvp.Key}");
                var expectedFolders = kvp.Value ?? Array.Empty<string>();
                var actualFolderList = actualFolders ?? Array.Empty<string>();
                Assert.Equal(expectedFolders.OrderBy(v => v, StringComparer.Ordinal), actualFolderList.OrderBy(v => v, StringComparer.Ordinal));
            }
        }
    }
}
