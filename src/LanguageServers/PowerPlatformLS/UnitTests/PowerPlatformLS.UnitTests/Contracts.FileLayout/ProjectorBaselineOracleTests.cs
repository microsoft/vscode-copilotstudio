namespace Microsoft.PowerPlatformLS.UnitTests.Contracts.FileLayout
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Xunit;

    /// <summary>
    /// Baseline tests that lock oracle constants without touching implementation.
    /// These ensure the oracle data itself remains unchanged.
    /// </summary>
    public class ProjectorBaselineOracleTests
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

        [Fact]
        public void OracleCases_AreLockedToBaseline()
        {
            AssertTheoryDataEqual(OracleCases, ProjectorOracleParityTests.OracleCases, OracleCaseKey);
        }

        [Fact]
        public void SubAgentFilePathCases_AreLockedToBaseline()
        {
            AssertTheoryDataEqual(SubAgentFilePathCases, ProjectorOracleParityTests.SubAgentFilePathCases, SubAgentCaseKey);
        }

        private static void AssertTheoryDataEqual<T1, T2, T3, T4, T5, T6, T7>(
            TheoryData<T1, T2, T3, T4, T5, T6, T7> expected,
            TheoryData<T1, T2, T3, T4, T5, T6, T7> actual,
            Func<object[], string> keySelector)
        {
            var expectedKeys = ToKeyList(expected, keySelector);
            var actualKeys = ToKeyList(actual, keySelector);

            Assert.Equal(expectedKeys.Count, actualKeys.Count);
            Assert.Equal(expectedKeys, actualKeys);
        }

        private static void AssertTheoryDataEqual<T1, T2, T3, T4>(
            TheoryData<T1, T2, T3, T4> expected,
            TheoryData<T1, T2, T3, T4> actual,
            Func<object[], string> keySelector)
        {
            var expectedKeys = ToKeyList(expected, keySelector);
            var actualKeys = ToKeyList(actual, keySelector);

            Assert.Equal(expectedKeys.Count, actualKeys.Count);
            Assert.Equal(expectedKeys, actualKeys);
        }

        private static List<string> ToKeyList(System.Collections.IEnumerable data, Func<object[], string> keySelector)
        {
            return data.Cast<object[]>()
                .Select(keySelector)
                .ToList();
        }

        private static string OracleCaseKey(object[] row)
        {
            var legacyArgs = row[5] is object[] args
                ? string.Join("|", args.Select(a => a?.ToString() ?? string.Empty))
                : string.Empty;

            return string.Join("|", new[]
            {
                row[0]?.ToString() ?? string.Empty,
                row[1]?.ToString() ?? string.Empty,
                row[2]?.ToString() ?? string.Empty,
                row[3]?.ToString() ?? string.Empty,
                row[4]?.ToString() ?? string.Empty,
                legacyArgs,
                row[6]?.ToString() ?? string.Empty,
            });
        }

        private static string SubAgentCaseKey(object[] row)
        {
            return string.Join("|", new[]
            {
                row[0]?.ToString() ?? string.Empty,
                row[1]?.ToString() ?? string.Empty,
                row[2]?.ToString() ?? string.Empty,
                row[3]?.ToString() ?? string.Empty,
            });
        }
    }
}
