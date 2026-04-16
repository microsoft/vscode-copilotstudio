namespace Microsoft.PowerPlatformLS.UnitTests.Contracts.FileLayout
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Microsoft.Agents.ObjectModel;
    using Microsoft.Agents.ObjectModel.FileProjection;
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.PowerPlatformLS.Contracts.FileLayout;
    using Xunit;

    /// <summary>
    /// Tests for folder/layout projection parity.
    /// </summary>
    /// <remarks>
    /// <para>Validates that:</para>
    /// <list type="bullet">
    /// <item>LspProjectionLayout.FileStructureMap contains expected folder → element-type mappings.</item>
    /// <item>All component types have layout entries.</item>
    /// <item>Legacy non-component entries are present.</item>
    /// </list>
    /// </remarks>
    [Trait("Category", "LayoutParity")]
    public class ProjectorLayoutParityTests
    {
        #region Layout Completeness

        /// <summary>
        /// Every component projector folder must be in LspProjectionLayout.FileStructureMap.
        /// </summary>
        [Fact]
        public void AllProjectorFolders_InFileStructureMap()
        {
            var registry = LspProjectorRegistry.Instance;
            var projectors = registry.GetAll().OfType<IComponentProjector>().ToList();

            var missingFolders = new List<string>();

            foreach (var projector in projectors)
            {
                var folder = LspProjection.GetRuleFolderForElementType(projector.ElementType) ?? projector.Folder;

                // Skip special cases handled differently
                if (projector.TargetType == typeof(DialogComponent) ||
                    projector.TargetType == typeof(GptComponent))
                {
                    continue;
                }

                if (!LspProjectionLayout.FileStructureMap.ContainsKey(folder))
                {
                    missingFolders.Add($"{projector.TargetType.Name} → {folder}");
                }
            }

            Assert.True(missingFolders.Count == 0, $"Missing layout entries:\n{string.Join("\n", missingFolders)}");
        }

        /// <summary>
        /// DialogComponent's polymorphic folders (topics/, actions/, agents/) must be in layout.
        /// </summary>
        [Fact]
        public void DialogComponent_PolymorphicFolders_InLayout()
        {
            Assert.True(LspProjectionLayout.FileStructureMap.ContainsKey("topics/"), "Missing topics/ folder");
            Assert.True(LspProjectionLayout.FileStructureMap.ContainsKey("actions/"), "Missing actions/ folder");
            Assert.True(LspProjectionLayout.FileStructureMap.ContainsKey("agents/"), "Missing agents/ folder");
            Assert.True(LspProjectionLayout.FileStructureMap.ContainsKey("agent"), "Missing agent file entry");
        }

        /// <summary>
        /// Legacy non-component entries must be present.
        /// </summary>
        [Theory]
        [InlineData("settings", typeof(BotEntity))]
        [InlineData("collection", typeof(BotComponentCollection))]
        [InlineData(".mcs/botdefinition.json", typeof(DefinitionBase))]
        [InlineData("connectionreferences", typeof(ConnectionReferencesSourceFile))]
        [InlineData("references", typeof(ReferencesSourceFile))]
        public void LegacyNonComponentEntries_Present(string key, Type expectedType)
        {
            Assert.True(LspProjectionLayout.FileStructureMap.ContainsKey(key), $"Missing entry: {key}");
            var types = LspProjectionLayout.FileStructureMap[key];
            Assert.Contains(expectedType, types);
        }

        /// <summary>
        /// icon.png entry must be present (empty type list).
        /// </summary>
        [Fact]
        public void IconPng_Entry_Present()
        {
            Assert.True(LspProjectionLayout.FileStructureMap.ContainsKey("icon.png"), "Missing icon.png entry");
            Assert.Empty(LspProjectionLayout.FileStructureMap["icon.png"]);
        }

        #endregion

        #region Folder → ElementType Mapping

        /// <summary>
        /// topics/ folder maps to AdaptiveDialog.
        /// </summary>
        [Fact]
        public void TopicsFolder_MapsTo_AdaptiveDialog()
        {
            Assert.True(LspProjectionLayout.FileStructureMap.ContainsKey("topics/"));
            var types = LspProjectionLayout.FileStructureMap["topics/"];
            Assert.Contains(typeof(AdaptiveDialog), types);
        }

        /// <summary>
        /// actions/ folder maps to TaskDialog.
        /// </summary>
        [Fact]
        public void ActionsFolder_MapsTo_TaskDialog()
        {
            Assert.True(LspProjectionLayout.FileStructureMap.ContainsKey("actions/"));
            var types = LspProjectionLayout.FileStructureMap["actions/"];
            Assert.Contains(typeof(TaskDialog), types);
        }

        /// <summary>
        /// agents/ folder maps to TaskDialog for connected-agent task dialogs.
        /// </summary>
        [Fact]
        public void AgentsFolder_MapsTo_TaskDialog()
        {
            Assert.True(LspProjectionLayout.FileStructureMap.ContainsKey("agents/"));
            var types = LspProjectionLayout.FileStructureMap["agents/"];
            Assert.Contains(typeof(TaskDialog), types);
        }

        /// <summary>
        /// translations/ folder maps to AdaptiveDialog.
        /// </summary>
        [Fact]
        public void TranslationsFolder_MapsTo_AdaptiveDialog()
        {
            Assert.True(LspProjectionLayout.FileStructureMap.ContainsKey("translations/"));
            var types = LspProjectionLayout.FileStructureMap["translations/"];
            Assert.Contains(typeof(AdaptiveDialog), types);
        }

        /// <summary>
        /// variables/ folder maps to Variable.
        /// </summary>
        [Fact]
        public void VariablesFolder_MapsTo_Variable()
        {
            Assert.True(LspProjectionLayout.FileStructureMap.ContainsKey("variables/"));
            var types = LspProjectionLayout.FileStructureMap["variables/"];
            Assert.Contains(typeof(Variable), types);
        }

        /// <summary>
        /// entities/ folder maps to EntityWithAnnotatedSamples.
        /// </summary>
        [Fact]
        public void EntitiesFolder_MapsTo_EntityWithAnnotatedSamples()
        {
            Assert.True(LspProjectionLayout.FileStructureMap.ContainsKey("entities/"));
            var types = LspProjectionLayout.FileStructureMap["entities/"];
            Assert.Contains(typeof(EntityWithAnnotatedSamples), types);
        }

        /// <summary>
        /// knowledge/ folder maps to KnowledgeSource.
        /// </summary>
        [Fact]
        public void KnowledgeFolder_MapsTo_KnowledgeSource()
        {
            Assert.True(LspProjectionLayout.FileStructureMap.ContainsKey("knowledge/"));
            var types = LspProjectionLayout.FileStructureMap["knowledge/"];
            Assert.Contains(typeof(KnowledgeSource), types);
        }

        /// <summary>
        /// knowledge/files/ folder maps to FileAttachmentComponent.
        /// </summary>
        [Fact]
        public void KnowledgeFilesFolder_MapsTo_FileAttachmentComponent()
        {
            Assert.True(LspProjectionLayout.FileStructureMap.ContainsKey("knowledge/files/"));
            var types = LspProjectionLayout.FileStructureMap["knowledge/files/"];
            Assert.Contains(typeof(FileAttachmentComponent), types);
        }

        /// <summary>
        /// skills/ folder maps to SkillDefinition.
        /// </summary>
        [Fact]
        public void SkillsFolder_MapsTo_SkillDefinition()
        {
            Assert.True(LspProjectionLayout.FileStructureMap.ContainsKey("skills/"));
            var types = LspProjectionLayout.FileStructureMap["skills/"];
            Assert.Contains(typeof(SkillDefinition), types);
        }

        /// <summary>
        /// trigger/ folder maps to ExternalTriggerConfiguration.
        /// </summary>
        [Fact]
        public void TriggerFolder_MapsTo_ExternalTriggerConfiguration()
        {
            Assert.True(LspProjectionLayout.FileStructureMap.ContainsKey("trigger/"));
            var types = LspProjectionLayout.FileStructureMap["trigger/"];
            Assert.Contains(typeof(ExternalTriggerConfiguration), types);
        }

        /// <summary>
        /// settings/ folder maps to BotSettingsBase.
        /// </summary>
        [Fact]
        public void SettingsFolder_MapsTo_BotSettingsBase()
        {
            Assert.True(LspProjectionLayout.FileStructureMap.ContainsKey("settings/"));
            var types = LspProjectionLayout.FileStructureMap["settings/"];
            Assert.Contains(typeof(BotSettingsBase), types);
        }

        #endregion

        #region TypeToFileCandidates Reverse Mapping

        /// <summary>
        /// AdaptiveDialog should map to topics/ and translations/.
        /// </summary>
        [Fact]
        public void AdaptiveDialog_MapsTo_TopicsAndTranslations()
        {
            Assert.True(LspProjectionLayout.TypeToFileCandidates.ContainsKey(typeof(AdaptiveDialog)));
            var folders = LspProjectionLayout.TypeToFileCandidates[typeof(AdaptiveDialog)];
            Assert.Contains("topics/", folders);
            Assert.Contains("translations/", folders);
        }

        /// <summary>
        /// TaskDialog should map to actions/ and agents/.
        /// </summary>
        [Fact]
        public void TaskDialog_MapsTo_ActionsAndAgents()
        {
            Assert.True(LspProjectionLayout.TypeToFileCandidates.ContainsKey(typeof(TaskDialog)));
            var folders = LspProjectionLayout.TypeToFileCandidates[typeof(TaskDialog)];
            Assert.Contains("actions/", folders);
            Assert.Contains("agents/", folders);
        }

        /// <summary>
        /// AgentDialog should map to "agent" (not agents/).
        /// </summary>
        [Fact]
        public void AgentDialog_MapsTo_Agent()
        {
            Assert.True(LspProjectionLayout.TypeToFileCandidates.ContainsKey(typeof(AgentDialog)));
            var folders = LspProjectionLayout.TypeToFileCandidates[typeof(AgentDialog)];
            Assert.Contains("agent", folders);
            // Legacy: should NOT contain "agents/" in TypeToFileCandidates
            Assert.DoesNotContain("agents/", folders);
        }

        #endregion
    }
}
