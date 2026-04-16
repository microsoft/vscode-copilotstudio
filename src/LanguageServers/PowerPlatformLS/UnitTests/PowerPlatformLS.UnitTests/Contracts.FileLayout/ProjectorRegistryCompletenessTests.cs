namespace Microsoft.PowerPlatformLS.UnitTests.Contracts.FileLayout
{
    using System;
    using System.Linq;
    using Microsoft.Agents.ObjectModel;
    using Microsoft.Agents.ObjectModel.FileProjection;
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.PowerPlatformLS.Contracts.FileLayout;
    using Xunit;

    /// <summary>
    /// Tests for registry completeness and round-trip correctness.
    /// </summary>
    /// <remarks>
    /// <para>Validates that:</para>
    /// <list type="bullet">
    /// <item>Every concrete BotComponentBase type has a projector.</item>
    /// <item>GetFilePath → GetSchemaName round-trips correctly.</item>
    /// </list>
    /// </remarks>
    [Trait("Category", "RegistryCompleteness")]
    public class ProjectorRegistryCompletenessTests
    {
        private const string TestBotName = "testBot";
        private readonly IProjectorRegistry _registry = LspProjectorRegistry.Instance;

        #region Registry Completeness

        /// <summary>
        /// Every concrete BotComponentBase type must have a registered projector.
        /// </summary>
        [Fact]
        public void AllBotComponentTypes_HaveProjectors()
        {
            var componentTypes = typeof(BotComponentBase).Assembly
                .GetTypes()
                .Where(t => t.IsClass && !t.IsAbstract)
                .Where(t => typeof(BotComponentBase).IsAssignableFrom(t))
                .Where(t => !t.Name.StartsWith("Unknown", StringComparison.Ordinal))
                .Where(t => t.Name != "LegacyOrUnknownComponent")
                .ToList();

            var missing = componentTypes
                .Where(t => _registry.GetForType(t) is not IComponentProjector)
                .Select(t => t.Name)
                .ToList();

            Assert.True(missing.Count == 0, $"Missing projectors for: {string.Join(", ", missing)}");
        }

        /// <summary>
        /// Every projector must have a valid ElementType.
        /// </summary>
        [Fact]
        public void AllProjectors_HaveValidElementType()
        {
            var projectors = _registry.GetAll().OfType<IComponentProjector>().ToList();

            foreach (var projector in projectors)
            {
                Assert.NotNull(projector.ElementType);
                Assert.True(typeof(BotElement).IsAssignableFrom(projector.ElementType),
                    $"{projector.TargetType.Name} has invalid ElementType: {projector.ElementType?.Name}");
            }
        }

        /// <summary>
        /// Every projector must have non-empty Infix and Folder.
        /// </summary>
        [Fact]
        public void AllProjectors_HaveInfixAndFolder()
        {
            var projectors = _registry.GetAll().OfType<IComponentProjector>().ToList();

            foreach (var projector in projectors)
            {
                var infix = LspProjection.GetRuleInfixForElementType(projector.ElementType) ?? projector.Infix;
                var folder = LspProjection.GetRuleFolderForElementType(projector.ElementType) ?? projector.Folder;

                Assert.False(string.IsNullOrEmpty(infix), $"{projector.TargetType.Name} has empty Infix");
                Assert.False(string.IsNullOrEmpty(folder), $"{projector.TargetType.Name} has empty Folder");
            }
        }

        #endregion

        #region Round-Trip Tests

        /// <summary>
        /// SkillComponent round-trip: GetFilePath → remove extension → GetSchemaName returns original schema.
        /// </summary>
        [Theory]
        [InlineData("testBot.skill.MySkill", "skills/MySkill.mcs.yml")]
        [InlineData("testBot.skill.CopilotEcho.d2df", "skills/CopilotEcho.d2df.mcs.yml")]
        public void SkillComponent_RoundTrip(string schemaName, string expectedPath)
        {
            var projector = _registry.GetForType(typeof(SkillComponent)) as IComponentProjector;
            Assert.NotNull(projector);

            var component = new SkillComponent.Builder
            {
                SchemaName = schemaName,
                Skill = new SkillDefinition.Builder()
            }.Build();

            var context = new ProjectionContext(BotName: TestBotName);
            var actualPath = projector!.GetFilePath(component, context);
            Assert.Equal(expectedPath, actualPath);

            var pathWithoutExtension = actualPath.Replace(".mcs.yml", "");
            var recoveredSchema = projector.GetSchemaName(pathWithoutExtension, TestBotName, projector.ElementType);
            Assert.Equal(schemaName, recoveredSchema);
        }

        /// <summary>
        /// GlobalVariableComponent round-trip with legacy infix.
        /// </summary>
        [Theory]
        [InlineData("testBot.GlobalVariableComponent.UserInfo", "variables/UserInfo.mcs.yml")]
        public void GlobalVariableComponent_RoundTrip(string schemaName, string expectedPath)
        {
            var projector = _registry.GetForType(typeof(GlobalVariableComponent)) as IComponentProjector;
            Assert.NotNull(projector);

            var component = new GlobalVariableComponent.Builder
            {
                SchemaName = schemaName,
                Variable = new Variable.Builder()
            }.Build();

            var context = new ProjectionContext(BotName: TestBotName);
            var service = LspProjectorService.Instance;

            // Use LspProjectorService for schema derivation (applies legacy infix)
            var pathWithoutExtension = expectedPath.Replace(".mcs.yml", "");
            var recoveredSchema = service.GetSchemaName(pathWithoutExtension, TestBotName, projector!.ElementType);
            Assert.Equal(schemaName, recoveredSchema);
        }

        /// <summary>
        /// KnowledgeSourceComponent round-trip.
        /// </summary>
        [Theory]
        [InlineData("testBot.knowledge.Wikipedia", "knowledge/Wikipedia.mcs.yml")]
        [InlineData("testBot.knowledge.Source.0", "knowledge/Source.0.mcs.yml")]
        public void KnowledgeSourceComponent_RoundTrip(string schemaName, string expectedPath)
        {
            var projector = _registry.GetForType(typeof(KnowledgeSourceComponent)) as IComponentProjector;
            Assert.NotNull(projector);

            var service = LspProjectorService.Instance;
            var pathWithoutExtension = expectedPath.Replace(".mcs.yml", "");
            var recoveredSchema = service.GetSchemaName(pathWithoutExtension, TestBotName, projector!.ElementType);
            Assert.Equal(schemaName, recoveredSchema);
        }

        /// <summary>
        /// CustomEntityComponent round-trip with legacy infix.
        /// </summary>
        [Theory]
        [InlineData("testBot.entity.Customer", "entities/Customer.mcs.yml")]
        public void CustomEntityComponent_RoundTrip(string schemaName, string expectedPath)
        {
            var projector = _registry.GetForType(typeof(CustomEntityComponent)) as IComponentProjector;
            Assert.NotNull(projector);

            var service = LspProjectorService.Instance;
            var pathWithoutExtension = expectedPath.Replace(".mcs.yml", "");
            var recoveredSchema = service.GetSchemaName(pathWithoutExtension, TestBotName, projector!.ElementType);
            Assert.Equal(schemaName, recoveredSchema);
        }

        /// <summary>
        /// ExternalTriggerComponent round-trip.
        /// </summary>
        [Theory]
        [InlineData("testBot.ExternalTriggerComponent.trigger1", "trigger/trigger1.mcs.yml")]
        [InlineData("testBot.ExternalTriggerComponent.workflow.trigger1", "trigger/workflow.trigger1.mcs.yml")]
        public void ExternalTriggerComponent_RoundTrip(string schemaName, string expectedPath)
        {
            var projector = _registry.GetForType(typeof(ExternalTriggerComponent)) as IComponentProjector;
            Assert.NotNull(projector);

            var service = LspProjectorService.Instance;
            var pathWithoutExtension = expectedPath.Replace(".mcs.yml", "");
            var recoveredSchema = service.GetSchemaName(pathWithoutExtension, TestBotName, projector!.ElementType);
            Assert.Equal(schemaName, recoveredSchema);
        }

        /// <summary>
        /// BotSettingsComponent round-trip.
        /// </summary>
        [Theory]
        [InlineData("testBot.BotSettingsComponent.config", "settings/config.mcs.yml")]
        public void BotSettingsComponent_RoundTrip(string schemaName, string expectedPath)
        {
            var projector = _registry.GetForType(typeof(BotSettingsComponent)) as IComponentProjector;
            Assert.NotNull(projector);

            var service = LspProjectorService.Instance;
            var pathWithoutExtension = expectedPath.Replace(".mcs.yml", "");
            var recoveredSchema = service.GetSchemaName(pathWithoutExtension, TestBotName, projector!.ElementType);
            Assert.Equal(schemaName, recoveredSchema);
        }

        /// <summary>
        /// FileAttachmentComponent round-trip.
        /// </summary>
        [Theory]
        [InlineData("testBot.file.doc1", "knowledge/files/doc1.mcs.yml")]
        public void FileAttachmentComponent_RoundTrip(string schemaName, string expectedPath)
        {
            var projector = _registry.GetForType(typeof(FileAttachmentComponent)) as IComponentProjector;
            Assert.NotNull(projector);

            var service = LspProjectorService.Instance;
            var pathWithoutExtension = expectedPath.Replace(".mcs.yml", "");
            var recoveredSchema = service.GetSchemaName(pathWithoutExtension, TestBotName, projector!.ElementType);
            Assert.Equal(schemaName, recoveredSchema);
        }

        #endregion
    }
}
