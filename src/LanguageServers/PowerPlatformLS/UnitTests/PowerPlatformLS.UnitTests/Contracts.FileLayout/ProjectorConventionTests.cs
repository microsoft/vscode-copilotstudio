namespace Microsoft.PowerPlatformLS.UnitTests.Contracts.FileLayout
{
    using System;
    using Microsoft.Agents.ObjectModel;
    using Microsoft.Agents.ObjectModel.FileProjection;
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.PowerPlatformLS.Contracts.FileLayout;
    using Xunit;

    /// <summary>
    /// Tests for codegen convention correctness.
    /// </summary>
    /// <remarks>
    /// <para>Validates that generator conventions (type name → infix/folder) produce expected values.</para>
    /// <para>Uses LspProjection to get effective values when legacy overrides exist.</para>
    /// </remarks>
    [Trait("Category", "CodegenConventions")]
    public class ProjectorConventionTests
    {
        private readonly IProjectorRegistry _registry = LspProjectorRegistry.Instance;

        #region Convention-Compliant Components

        /// <summary>
        /// SkillComponent follows convention: .skill. / skills/
        /// </summary>
        [Fact]
        public void SkillComponent_FollowsConvention()
        {
            var projector = _registry.GetForType(typeof(SkillComponent)) as IComponentProjector;
            Assert.NotNull(projector);

            // SkillComponent is convention-compliant, no legacy override
            Assert.Equal(".skill.", projector!.Infix);
            Assert.Equal("skills/", projector.Folder);
            Assert.Equal(typeof(SkillDefinition), projector.ElementType);
        }

        /// <summary>
        /// TestCaseComponent follows convention: .testcase. / testcases/
        /// </summary>
        [Fact]
        public void TestCaseComponent_FollowsConvention()
        {
            var projector = _registry.GetForType(typeof(TestCaseComponent)) as IComponentProjector;
            Assert.NotNull(projector);

            Assert.Equal(".testcase.", projector!.Infix);
            Assert.Equal("testcases/", projector.Folder);
        }

        #endregion

        #region Components with Legacy Overrides

        /// <summary>
        /// GlobalVariableComponent has legacy infix override: .GlobalVariableComponent.
        /// ObjectModel convention produces: .globalvariable. / variables/ (PluralForm="variables")
        /// LspProjection overrides the infix to use legacy naming.
        /// </summary>
        [Fact]
        public void GlobalVariableComponent_HasLegacyOverride()
        {
            var projector = _registry.GetForType(typeof(GlobalVariableComponent)) as IComponentProjector;
            Assert.NotNull(projector);

            // Generator produces convention values (folder now correct via PluralForm)
            Assert.Equal(".globalvariable.", projector!.Infix);
            Assert.Equal("variables/", projector.Folder);

            // Legacy metadata overrides the infix only
            var infix = LspProjection.GetRuleInfixForElementType(projector.ElementType);
            var folder = LspProjection.GetRuleFolderForElementType(projector.ElementType);
            Assert.Equal(".GlobalVariableComponent.", infix);
            Assert.Equal("variables/", folder);
        }

        /// <summary>
        /// BotSettingsComponent has legacy override: .BotSettingsComponent. / settings/
        /// </summary>
        [Fact]
        public void BotSettingsComponent_HasLegacyOverride()
        {
            var projector = _registry.GetForType(typeof(BotSettingsComponent)) as IComponentProjector;
            Assert.NotNull(projector);

            var infix = LspProjection.GetRuleInfixForElementType(projector!.ElementType);
            var folder = LspProjection.GetRuleFolderForElementType(projector.ElementType);
            Assert.Equal(".BotSettingsComponent.", infix);
            Assert.Equal("settings/", folder);
        }

        /// <summary>
        /// CustomEntityComponent has legacy override: .entity. / entities/
        /// </summary>
        [Fact]
        public void CustomEntityComponent_HasLegacyOverride()
        {
            var projector = _registry.GetForType(typeof(CustomEntityComponent)) as IComponentProjector;
            Assert.NotNull(projector);

            var infix = LspProjection.GetRuleInfixForElementType(projector!.ElementType);
            var folder = LspProjection.GetRuleFolderForElementType(projector.ElementType);
            Assert.Equal(".entity.", infix);
            Assert.Equal("entities/", folder);
        }

        /// <summary>
        /// ExternalTriggerComponent has legacy override: .ExternalTriggerComponent. / trigger/
        /// </summary>
        [Fact]
        public void ExternalTriggerComponent_HasLegacyOverride()
        {
            var projector = _registry.GetForType(typeof(ExternalTriggerComponent)) as IComponentProjector;
            Assert.NotNull(projector);

            var infix = LspProjection.GetRuleInfixForElementType(projector!.ElementType);
            var folder = LspProjection.GetRuleFolderForElementType(projector.ElementType);
            Assert.Equal(".ExternalTriggerComponent.", infix);
            Assert.Equal("trigger/", folder);
        }

        /// <summary>
        /// KnowledgeSourceComponent has legacy override: .knowledge. / knowledge/
        /// </summary>
        [Fact]
        public void KnowledgeSourceComponent_HasLegacyOverride()
        {
            var projector = _registry.GetForType(typeof(KnowledgeSourceComponent)) as IComponentProjector;
            Assert.NotNull(projector);

            var infix = LspProjection.GetRuleInfixForElementType(projector!.ElementType);
            var folder = LspProjection.GetRuleFolderForElementType(projector.ElementType);
            Assert.Equal(".knowledge.", infix);
            Assert.Equal("knowledge/", folder);
        }

        /// <summary>
        /// FileAttachmentComponent has legacy override: .file. / knowledge/files/
        /// </summary>
        [Fact]
        public void FileAttachmentComponent_HasLegacyOverride()
        {
            var projector = _registry.GetForType(typeof(FileAttachmentComponent)) as IComponentProjector;
            Assert.NotNull(projector);

            var infix = LspProjection.GetRuleInfixForElementType(projector!.ElementType);
            var folder = LspProjection.GetRuleFolderForElementType(projector.ElementType);
            Assert.Equal(".file.", infix);
            Assert.Equal("knowledge/files/", folder);
        }

        /// <summary>
        /// TranslationsComponent has legacy override: .topic. / translations/
        /// </summary>
        [Fact]
        public void TranslationsComponent_HasLegacyOverride()
        {
            var projector = _registry.GetForType(typeof(TranslationsComponent)) as IComponentProjector;
            Assert.NotNull(projector);

            var infix = LspProjection.GetRuleInfixForElementType(projector!.ElementType);
            var folder = LspProjection.GetRuleFolderForElementType(projector.ElementType);
            Assert.Equal(".topic.", infix);
            Assert.Equal("translations/", folder);
        }

        #endregion

        #region Polymorphic Components

        /// <summary>
        /// DialogComponent is marked as polymorphic.
        /// </summary>
        [Fact]
        public void DialogComponent_IsPolymorphic()
        {
            var projector = _registry.GetForType(typeof(DialogComponent)) as IComponentProjector;
            Assert.NotNull(projector);
            Assert.True(projector!.IsPolymorphic);
        }

        #endregion

        #region ElementType Mapping

        /// <summary>
        /// Projector ElementType matches expected deserialized types.
        /// </summary>
        [Theory]
        [InlineData(typeof(SkillComponent), typeof(SkillDefinition))]
        [InlineData(typeof(GlobalVariableComponent), typeof(Variable))]
        [InlineData(typeof(KnowledgeSourceComponent), typeof(KnowledgeSourceConfiguration))]
        [InlineData(typeof(ExternalTriggerComponent), typeof(ExternalTriggerConfiguration))]
        [InlineData(typeof(GptComponent), typeof(GptComponentMetadata))]
        public void Projector_ElementType_MatchesExpected(Type componentType, Type expectedElementType)
        {
            var projector = _registry.GetForType(componentType) as IComponentProjector;
            Assert.NotNull(projector);
            Assert.Equal(expectedElementType, projector!.ElementType);
        }

        #endregion

        #region Effective Infix/Folder (combining convention + override)

        /// <summary>
        /// GetEffectiveInfix returns legacy override when present, convention otherwise.
        /// </summary>
        [Theory]
        [InlineData(typeof(SkillComponent), ".skill.")]
        [InlineData(typeof(GlobalVariableComponent), ".GlobalVariableComponent.")]
        [InlineData(typeof(CustomEntityComponent), ".entity.")]
        [InlineData(typeof(KnowledgeSourceComponent), ".knowledge.")]
        public void GetEffectiveInfix_ReturnsCorrectValue(Type componentType, string expectedInfix)
        {
            var projector = _registry.GetForType(componentType) as IComponentProjector;
            Assert.NotNull(projector);

            var effectiveInfix = LspProjection.GetRuleInfixForElementType(projector!.ElementType) ?? projector.Infix;
            Assert.Equal(expectedInfix, effectiveInfix);
        }

        /// <summary>
        /// GetEffectiveFolder returns legacy override when present, convention otherwise.
        /// </summary>
        [Theory]
        [InlineData(typeof(SkillComponent), "skills/")]
        [InlineData(typeof(GlobalVariableComponent), "variables/")]
        [InlineData(typeof(CustomEntityComponent), "entities/")]
        [InlineData(typeof(KnowledgeSourceComponent), "knowledge/")]
        [InlineData(typeof(FileAttachmentComponent), "knowledge/files/")]
        public void GetEffectiveFolder_ReturnsCorrectValue(Type componentType, string expectedFolder)
        {
            var projector = _registry.GetForType(componentType) as IComponentProjector;
            Assert.NotNull(projector);

            var effectiveFolder = LspProjection.GetRuleFolderForElementType(projector!.ElementType) ?? projector.Folder;
            Assert.Equal(expectedFolder, effectiveFolder);
        }

        #endregion
    }
}
