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
    /// Tests that verify the projector system produces correct results.
    /// </summary>
    /// <remarks>
    /// <para>These tests validate LspProjectorService behavior against oracle constants.</para>
    /// <para>Oracle constants were captured from the legacy FileNameHelper implementation.</para>
    /// </remarks>
    [Trait("Category", "Projection")]
    public class ProjectorComparisonTests
    {
        private const string TestBotName = "cr5f7_agent6eFv9_s";
        private readonly IProjectorRegistry _registry = LspProjectorRegistry.Instance;
        private readonly LspProjectorService _service = LspProjectorService.Instance;

        #region Schema Name Comparison Tests

        /// <summary>
        /// Verifies DialogComponentProjector (AdaptiveDialog) produces correct schema names.
        /// </summary>
        [Theory]
        [InlineData("Greeting", "cr5f7_agent6eFv9_s.topic.Greeting")]
        [InlineData("Farewell", "cr5f7_agent6eFv9_s.topic.Farewell")]
        [InlineData("HelloWorld", "cr5f7_agent6eFv9_s.topic.HelloWorld")]
        public void TopicProjector_ProducesCorrectSchemaName(string fileName, string expectedSchemaName)
        {
            var projector = _registry.GetForType(typeof(DialogComponent)) as IComponentProjector;
            Assert.NotNull(projector);

            var projectorResult = _service.GetSchemaName($"topics/{fileName}", TestBotName, typeof(AdaptiveDialog));

            Assert.Equal(expectedSchemaName, projectorResult);
        }


        /// <summary>
        /// Verifies DialogComponentProjector (TaskDialog) produces correct schema names.
        /// </summary>
        [Theory]
        [InlineData("DoSomething", "cr5f7_agent6eFv9_s.action.DoSomething", "actions")]
        [InlineData("RunTask", "cr5f7_agent6eFv9_s.action.RunTask", "actions")]
        [InlineData("MyAction.docx", "cr5f7_agent6eFv9_s.action.MyAction.docx", "actions")]
        [InlineData("MyAction.v1.d1.txt", "cr5f7_agent6eFv9_s.action.MyAction.v1.d1.txt", "actions")]
        [InlineData("AgentA2Thisislongnameaspossible123", "cr5f7_agent6eFv9_s.InvokeConnectedAgentTaskAction.AgentA2Thisislongnameaspossible123", "agents")]
        [InlineData("AgentA2Thisislongnameaspossible123.v2", "cr5f7_agent6eFv9_s.InvokeConnectedAgentTaskAction.AgentA2Thisislongnameaspossible123.v2", "agents")]
        public void ActionProjector_ProducesCorrectSchemaName(string fileName, string expectedSchemaName, string expectedFolder)
        {
            var projector = _registry.GetForType(typeof(DialogComponent)) as IComponentProjector;
            Assert.NotNull(projector);

            var projectorResult = _service.GetSchemaName($"{expectedFolder}/{fileName}", TestBotName, typeof(TaskDialog));

            Assert.Equal(expectedSchemaName, projectorResult);

            if (projector != null)
            {
                var component = new DialogComponent.Builder
                {
                    SchemaName = expectedSchemaName,
                    Dialog = new TaskDialog.Builder()
                    {
                        ModelDisplayName = "Test TaskDialog",
                        ModelDescription = "Test TaskDialog Description"
                    }
                }.Build();

                var context = new ProjectionContext { BotName = TestBotName };
                var actualPath = _service.GetFilePath(component, context);
                var expectedPath = $"{expectedFolder}/{fileName}.mcs.yml";
                Assert.Equal(expectedPath, actualPath);
            }
        }

        /// <summary>
        /// Verifies GlobalVariableComponentProjector produces correct schema names.
        /// </summary>
        [Theory]
        [InlineData("var1", "cr5f7_agent6eFv9_s.GlobalVariableComponent.var1")]
        [InlineData("userInfo", "cr5f7_agent6eFv9_s.GlobalVariableComponent.userInfo")]
        public void VariableProjector_ProducesCorrectSchemaName(string fileName, string expectedSchemaName)
        {
            var projector = _registry.GetForType(typeof(GlobalVariableComponent)) as IComponentProjector;
            Assert.NotNull(projector);

            Assert.Equal(".GlobalVariableComponent.", GetRuleInfix(projector!));

            var projectorResult = _service.GetSchemaName($"variables/{fileName}", TestBotName, projector!.ElementType);

            Assert.Equal(expectedSchemaName, projectorResult);
        }

        /// <summary>
        /// Verifies BotSettingsComponentProjector produces correct schema names.
        /// </summary>
        [Theory]
        [InlineData("settings", "cr5f7_agent6eFv9_s.BotSettingsComponent.settings")]
        [InlineData("custom", "cr5f7_agent6eFv9_s.BotSettingsComponent.custom")]
        public void SettingsProjector_ProducesCorrectSchemaName(string fileName, string expectedSchemaName)
        {
            var projector = _registry.GetForType(typeof(BotSettingsComponent)) as IComponentProjector;
            Assert.NotNull(projector);

            Assert.Equal(".BotSettingsComponent.", GetRuleInfix(projector!));

            var projectorResult = _service.GetSchemaName($"settings/{fileName}", TestBotName, projector!.ElementType);

            Assert.Equal(expectedSchemaName, projectorResult);
        }

        /// <summary>
        /// Verifies CustomEntityComponentProjector produces correct schema names.
        /// </summary>
        [Theory]
        [InlineData("entity1", "cr5f7_agent6eFv9_s.entity.entity1")]
        [InlineData("Customer", "cr5f7_agent6eFv9_s.entity.Customer")]
        public void EntityProjector_ProducesCorrectSchemaName(string fileName, string expectedSchemaName)
        {
            var projector = _registry.GetForType(typeof(CustomEntityComponent)) as IComponentProjector;
            Assert.NotNull(projector);

            Assert.Equal(".entity.", GetRuleInfix(projector!));

            var projectorResult = _service.GetSchemaName($"entities/{fileName}", TestBotName, projector!.ElementType);

            Assert.Equal(expectedSchemaName, projectorResult);
        }

        /// <summary>
        /// Verifies FileAttachmentComponentProjector produces correct schema names.
        /// </summary>
        [Theory]
        [InlineData("file1", "cr5f7_agent6eFv9_s.file.file1")]
        [InlineData("document", "cr5f7_agent6eFv9_s.file.document")]
        public void FileAttachmentProjector_ProducesCorrectSchemaName(string fileName, string expectedSchemaName)
        {
            var projector = _registry.GetForType(typeof(FileAttachmentComponent)) as IComponentProjector;
            Assert.NotNull(projector);

            Assert.Equal(".file.", GetRuleInfix(projector!));

            var projectorResult = _service.GetSchemaName($"knowledge/files/{fileName}", TestBotName, projector!.ElementType);

            Assert.Equal(expectedSchemaName, projectorResult);
        }

        /// <summary>
        /// Verifies ExternalTriggerComponentProjector produces correct schema names.
        /// </summary>
        [Theory]
        [InlineData("trigger1", "cr5f7_agent6eFv9_s.ExternalTriggerComponent.trigger1")]
        [InlineData("workflow.trigger1", "cr5f7_agent6eFv9_s.ExternalTriggerComponent.workflow.trigger1")]
        public void ExternalTriggerProjector_ProducesCorrectSchemaName(string fileName, string expectedSchemaName)
        {
            var projector = _registry.GetForType(typeof(ExternalTriggerComponent)) as IComponentProjector;
            Assert.NotNull(projector);

            Assert.Equal(".ExternalTriggerComponent.", GetRuleInfix(projector!));

            var projectorResult = _service.GetSchemaName($"trigger/{fileName}", TestBotName, projector!.ElementType);

            Assert.Equal(expectedSchemaName, projectorResult);
        }

        /// <summary>
        /// Verifies SkillComponentProjector produces correct schema names.
        /// </summary>
        [Theory]
        [InlineData("copilotStudioSkill", "cr5f7_agent6eFv9_s.skill.copilotStudioSkill")]
        [InlineData("copilotStudioSkill.d2df", "cr5f7_agent6eFv9_s.skill.copilotStudioSkill.d2df")]
        public void SkillProjector_ProducesCorrectSchemaName(string fileName, string expectedSchemaName)
        {
            var projector = _registry.GetForType(typeof(SkillComponent)) as IComponentProjector;
            Assert.NotNull(projector);

            Assert.Equal(".skill.", GetRuleInfix(projector!));

            var projectorResult = _service.GetSchemaName($"skills/{fileName}", TestBotName, projector!.ElementType);

            Assert.Equal(expectedSchemaName, projectorResult);
        }

        /// <summary>
        /// Verifies GptComponentProjector produces FIXED schema name "gpt.default", IGNORING the filename.
        /// </summary>
        [Fact]
        public void GptProjector_ProducesFixedSchemaName()
        {
            var projector = _registry.GetForType(typeof(GptComponent)) as IComponentProjector;
            Assert.NotNull(projector);

            // GptComponent is special - it should ignore the filename and return strict default
            var projectorResult = _service.GetSchemaName("agent", TestBotName, projector!.ElementType);

            Assert.Equal("cr5f7_agent6eFv9_s.gpt.default", projectorResult);
        }

        /// <summary>
        /// Verifies AgentDialog schema name is derived from folder path, ignoring filename.
        /// </summary>
        [Theory]
        [InlineData("SubAgent1", "agent.mcs.yml", "cr5f7_agent6eFv9_s.agent.SubAgent1")]    // Standard agent file
        [InlineData("SubAgent1", "custom.mcs.yml", "cr5f7_agent6eFv9_s.agent.SubAgent1")]   // Non-standard filename
        [InlineData("MyChildAgent", "agent.mcs.yml", "cr5f7_agent6eFv9_s.agent.MyChildAgent")] // Different agent name
        public void AgentDialogProjector_ExtractsAgentName_FromPath(string subAgentName, string fileName, string expectedSchemaName)
        {
            var relativePathStr = $"agents/{subAgentName}/{fileName}";
            var pathNoExt = relativePathStr.Replace(".mcs.yml", "");
            var projectorResult = _service.GetSchemaName(pathNoExt, TestBotName, typeof(AgentDialog));

            Assert.Equal(expectedSchemaName, projectorResult);
        }

        /// <summary>
        /// Projector throws when path is not in the agents folder.
        /// </summary>
        [Fact]
        public void AgentDialogProjector_Throws_WhenPathNotInAgentsFolder()
        {
            // Normative: enforce invariant that AgentDialog schema derivation is only valid under agents/.
            // This avoids silently treating arbitrary paths as sub-agents.
            var relativePath = new AgentFilePath("topics/agent.mcs.yml");
            var pathNoExt = relativePath.RemoveExtension().ToString();

            Assert.Throws<InvalidOperationException>(
                () => _service.GetSchemaName(pathNoExt, TestBotName, typeof(AgentDialog)));
        }

        /// <summary>
        /// Verifies KnowledgeSourceComponentProjector produces correct schema names.
        /// </summary>
        [Theory]
        [InlineData("knowledgetest", "cr5f7_agent6eFv9_s.knowledge.knowledgetest")]
        [InlineData("PublicSiteSearchSource.0", "cr5f7_agent6eFv9_s.knowledge.PublicSiteSearchSource.0")]
        public void KnowledgeProjector_ProducesCorrectSchemaName(string fileName, string expectedSchemaName)
        {
            var projector = _registry.GetForType(typeof(KnowledgeSourceComponent)) as IComponentProjector;
            Assert.NotNull(projector);

            Assert.Equal(".knowledge.", GetRuleInfix(projector!));

            var projectorResult = _service.GetSchemaName($"knowledge/{fileName}", TestBotName, projector!.ElementType);

            Assert.Equal(expectedSchemaName, projectorResult);
        }

        #endregion

        #region Legacy Layout Parity Tests

        /// <summary>
        /// Legacy layout only exposes "agent" for AgentDialog files.
        /// This test documents the old behavior and will fail if "agents/" is added.
        /// </summary>
        [Fact]
        public void AgentDialog_FileCandidates_Match_LegacyLayout()
        {
            var candidates = LspProjectionLayout.TypeToFileCandidates[typeof(AgentDialog)];

            Assert.Contains("agent", candidates);
            Assert.DoesNotContain("agents/", candidates);
            Assert.Single(candidates);
        }

        #endregion

        #region FileStructureMap Completeness Tests

        /// <summary>
        /// Verifies each folder type in FileStructureMap has a corresponding projector.
        /// </summary>
        [Fact]
        public void AllFileStructureMapFolders_HaveProjectors()
        {
            foreach (var kvp in LspProjectionLayout.FileStructureMap)
            {
                var pathKey = kvp.Key;
                var types = kvp.Value;

                // Skip non-folder entries and non-component types
                if (!pathKey.EndsWith("/", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (var type in types)
                {
                    // Skip non-BotComponentBase types
                    if (!typeof(BotElement).IsAssignableFrom(type))
                    {
                        continue;
                    }

                    // These are element types, not component types
                    // Verify we can get projector for the containing component
                    var componentType = GetComponentTypeForElement(type);
                    if (componentType != null)
                    {
                        var projector = _registry.GetForType(componentType);
                        Assert.NotNull(projector);
                    }
                }
            }
        }

        private static Type? GetComponentTypeForElement(Type elementType)
        {
            // Map element types to component types
            return elementType.Name switch
            {
                nameof(AdaptiveDialog) or nameof(TaskDialog) or nameof(AgentDialog) => typeof(DialogComponent),
                nameof(Variable) => typeof(GlobalVariableComponent),
                nameof(EntityWithAnnotatedSamples) or "Entity" => typeof(CustomEntityComponent),
                nameof(BotSettingsBase) => typeof(BotSettingsComponent),
                nameof(ExternalTriggerConfiguration) => typeof(ExternalTriggerComponent),
                nameof(SkillDefinition) => typeof(SkillComponent),
                nameof(KnowledgeSource) or nameof(KnowledgeSourceConfiguration) => typeof(KnowledgeSourceComponent),
                nameof(FileAttachmentComponent) or nameof(FileAttachmentComponentMetadata) => typeof(FileAttachmentComponent),
                nameof(GptComponentMetadata) => typeof(GptComponent),
                _ => null,
            };
        }

        #endregion

        #region Infix Verification Tests

        /// <summary>
        /// Comprehensive test of all projector infixes against FileNameHelper.SchemaNameReference.
        /// </summary>
        [Fact]
        public void AllProjectorInfixes_Match_SchemaNameReference()
        {
            // These expected values come from FileNameHelper.SchemaNameReference
            var expectedInfixes = new (Type ComponentType, string ExpectedInfix)[]
            {
                (typeof(GlobalVariableComponent), ".GlobalVariableComponent."),
                (typeof(BotSettingsComponent), ".BotSettingsComponent."),
                (typeof(CustomEntityComponent), ".entity."),
                (typeof(FileAttachmentComponent), ".file."),
                (typeof(ExternalTriggerComponent), ".ExternalTriggerComponent."),
                (typeof(SkillComponent), ".skill."),
                (typeof(KnowledgeSourceComponent), ".knowledge."),
                (typeof(GptComponent), ".gpt."),
            };

            foreach (var (componentType, expectedInfix) in expectedInfixes)
            {
                var projector = _registry.GetForType(componentType) as IComponentProjector;
                Assert.NotNull(projector);
                Assert.Equal(expectedInfix, GetRuleInfix(projector!));
            }
        }

        #endregion

        #region Special Case Tests

        /// <summary>
        /// Verifies handling of already-qualified schema names in file names.
        /// These are the special cases where the file name already contains the full schema name.
        /// </summary>
        [Theory]
        [InlineData("agent1.topic.MyTopic", "topics/")]
        [InlineData("agent1.action.MyAction", "actions/")]
        [InlineData("agent1.knowledge.Source.0", "knowledge/")]
        [InlineData("agent1.file.MyLedger.xlsx_hyG", "knowledge/files/")]
        [InlineData("agent1.ExternalTriggerComponent.Workflow.Trigger", "trigger/")]
        [InlineData("agent1.skill.MySkill", "skills/")]
        public void AlreadyQualifiedFileNames_PassThrough(string qualifiedFileName, string folder)
        {
            // Already-qualified names should pass through unchanged
            // Determine element type from the qualified name
            var elementType = qualifiedFileName.Contains(".topic.") ? typeof(AdaptiveDialog)
                : qualifiedFileName.Contains(".action.") ? typeof(TaskDialog)
                : qualifiedFileName.Contains(".knowledge.") ? typeof(KnowledgeSource)
                : qualifiedFileName.Contains(".file.") ? typeof(FileAttachmentComponent)
                : qualifiedFileName.Contains(".ExternalTriggerComponent.") ? typeof(ExternalTriggerConfiguration)
                : qualifiedFileName.Contains(".skill.") ? typeof(SkillDefinition)
                : throw new InvalidOperationException($"Unknown infix in: {qualifiedFileName}");

            var projectorResult = _service.GetSchemaName($"{folder}{qualifiedFileName}", "agent1", elementType);

            Assert.Equal(qualifiedFileName, projectorResult);
        }

        /// <summary>
        /// Verifies knowledge files that already contain a .topic. infix preserve the schema name.
        /// </summary>
        [Fact]
        public void KnowledgeFile_WithTopicInfix_PreservesSchemaName()
        {
            var fileName = "agent1.topic.SomeTopic";

            var projector = _registry.GetForType(typeof(KnowledgeSourceComponent)) as IComponentProjector;
            Assert.NotNull(projector);

            var projectorResult = _service.GetSchemaName($"knowledge/{fileName}", "agent1", projector!.ElementType);

            // Already-qualified name passes through unchanged
            Assert.Equal(fileName, projectorResult);
        }

        /// <summary>
        /// Verifies dotted short names preserve expected schema naming behavior.
        /// </summary>
        [Fact]
        public void DottedNames_ProduceCorrectSchemaName()
        {
            // Variables with dots pass through unchanged (no DotPassthrough for variables)
            var variableResult = _service.GetSchemaName("variables/user.profile", TestBotName, typeof(Variable));
            Assert.Equal("user.profile", variableResult);

            // Settings with dots pass through unchanged (no DotPassthrough for settings)
            var settingsResult = _service.GetSchemaName("settings/custom.v2", TestBotName, typeof(BotSettingsBase));
            Assert.Equal("custom.v2", settingsResult);

            // Entities with dots also pass through unchanged (no DotPassthrough for entities)
            var entityResult = _service.GetSchemaName("entities/Customer.VIP", TestBotName, typeof(Entity));
            Assert.Equal("Customer.VIP", entityResult);
        }

        /// <summary>
        /// Regression: Components with schema names containing unexpected infixes must round-trip unchanged.
        /// DeriveShortName was stripping bot prefix when expected infix wasn't found.
        /// </summary>
        [Theory]
        [InlineData(typeof(KnowledgeSourceComponent), "knowledge/", "agent1.topic.CollabHome_abc")]  // .topic. instead of .knowledge.
        [InlineData(typeof(KnowledgeSourceComponent), "knowledge/", "agent1.action.SomeAction_xyz")] // .action. instead of .knowledge.
        [InlineData(typeof(CustomEntityComponent), "entities/", "agent1.topic.EntityName")]          // .topic. instead of .entity.
        [InlineData(typeof(GlobalVariableComponent), "variables/", "agent1.topic.VarName")]          // .topic. instead of .GlobalVariableComponent.
        public void Component_WithMismatchedInfix_RoundTrips_SchemaNameUnchanged(Type componentType, string expectedFolder, string schemaName)
        {
            var component = CreateComponent(componentType, schemaName);
            var context = new ProjectionContext("agent1");
            
            // Schema → Path
            var filePath = _service.GetFilePath(component, context);
            Assert.NotNull(filePath);
            Assert.Equal($"{expectedFolder}{schemaName}.mcs.yml", filePath);

            // Path → Schema (full round-trip)
            var projector = _registry.GetForType(componentType) as IComponentProjector;
            Assert.NotNull(projector);
            var pathWithoutExt = filePath!.Replace(".mcs.yml", "");
            var recoveredSchema = _service.GetSchemaName(pathWithoutExt, "agent1", projector!.ElementType);
            Assert.Equal(schemaName, recoveredSchema);
        }

        private static BotComponentBase CreateComponent(Type type, string schemaName) => type.Name switch
        {
            nameof(KnowledgeSourceComponent) => new KnowledgeSourceComponent(schemaName: schemaName, displayName: "Test", description: "Test"),
            nameof(CustomEntityComponent) => new CustomEntityComponent.Builder { SchemaName = schemaName, Entity = new ClosedListEntity.Builder() }.Build(),
            nameof(GlobalVariableComponent) => new GlobalVariableComponent.Builder { SchemaName = schemaName, Variable = new Variable.Builder() }.Build(),
            _ => throw new ArgumentOutOfRangeException(nameof(type))
        };

        /// <summary>
        /// Verifies GPT schema name ignores filename and always uses gpt.default.
        /// </summary>
        [Fact]
        public void GptFile_AlwaysReturnsGptDefault()
        {
            var fileName = "agent1.gpt.Custom";

            var projector = _registry.GetForType(typeof(GptComponent)) as IComponentProjector;
            Assert.NotNull(projector);

            var projectorResult = _service.GetSchemaName($"{fileName}", "agent1", projector!.ElementType);

            // Always returns gpt.default regardless of filename
            Assert.Equal("agent1.gpt.default", projectorResult);
        }

        /// <summary>
        /// Verifies translations path schema naming produces correct topic schema name.
        /// </summary>
        [Fact]
        public void TranslationsPath_ProducesCorrectTopicSchemaName()
        {
            var fileName = "MyTopic";
            var expectedSchemaName = $"{TestBotName}.topic.{fileName}";

            var projector = _registry.GetForElementType(typeof(AdaptiveDialog), $"translations/{fileName}");
            Assert.NotNull(projector);

            var projectorResult = _service.GetSchemaName($"translations/{fileName}", TestBotName, typeof(AdaptiveDialog));

            Assert.Equal(expectedSchemaName, projectorResult);
        }

        /// <summary>
        /// Verifies LspProjection schema name is correct for translations path.
        /// </summary>
        [Fact]
        public void TranslationsPath_LspProjection_ProducesCorrectSchemaName()
        {
            var fileName = "MyTopic";
            var expectedSchemaName = $"{TestBotName}.topic.{fileName}";

            var lspResult = LspProjection.GetSchemaName($"translations/{fileName}", TestBotName, typeof(AdaptiveDialog));

            Assert.Equal(expectedSchemaName, lspResult);
        }

        /// <summary>
        /// Verifies topic names with dots are handled correctly.
        /// </summary>
        [Theory]
        [InlineData("Greeting.pt-BR", "cr5f7_agent6eFv9_s.topic.Greeting.pt-BR")]
        public void TopicWithDot_ProducesCorrectSchemaName(string fileName, string expectedSchemaName)
        {
            var projector = _registry.GetForType(typeof(DialogComponent)) as IComponentProjector;
            Assert.NotNull(projector);
            var projectorResult = _service.GetSchemaName($"topics/{fileName}", TestBotName, typeof(AdaptiveDialog));
            Assert.Equal(expectedSchemaName, projectorResult);
        }

        #endregion

        #region Auto-Support Tests (Relaxation #2 Validation)

        /// <summary>
        /// Verifies that convention-compliant components are automatically available
        /// in the LS without explicit LS-specific overrides.
        /// </summary>
        /// <remarks>
        /// <para>This test validates "Relaxation #2" from the reviewer spec:</para>
        /// <para>"LS layout and routing derive from the full default projector registry.
        /// New ObjectModel components become available without LS edits when convention‑compliant."</para>
        /// <para>A component is convention-compliant if it:</para>
        /// <list type="bullet">
        /// <item>Has a projector in DefaultProjectorRegistry</item>
        /// <item>Uses ObjectModel default behavior for GetFilePath, GetSchemaName, CreateComponent</item>
        /// </list>
        /// </remarks>
        [Fact]
        public void ConventionCompliantComponents_AutomaticallyAvailable_WithoutLsOverrides()
        {
            // These components use ObjectModel defaults and are auto-supported in the LS
            var conventionCompliantComponents = new[]
            {
                typeof(SkillComponent),
                typeof(TestCaseComponent),
            };

            foreach (var componentType in conventionCompliantComponents)
            {
                // Verify: Component IS available in the registry
                var projector = _registry.GetForType(componentType) as IComponentProjector;
                Assert.NotNull(projector);

                // Verify: Projector produces valid paths and schema names
                Assert.False(string.IsNullOrEmpty(projector!.Folder));
                Assert.False(string.IsNullOrEmpty(GetRuleInfix(projector)));

                // Verify: Schema name derivation works (uses ObjectModel default)
                var testFileName = "TestItem";
                var schemaName = projector.GetSchemaName($"{projector.Folder}{testFileName}", TestBotName, projector.ElementType);
                Assert.StartsWith(TestBotName, schemaName);
                Assert.Contains(GetRuleInfix(projector), schemaName);
                Assert.EndsWith(testFileName, schemaName);
            }
        }

        /// <summary>
        /// Verifies that SkillComponent (convention-compliant) routes correctly through LS registry
        /// without any LS-specific override code.
        /// </summary>
        [Theory]
        [InlineData("MySkill", "skills/MySkill.mcs.yml", "cr5f7_agent6eFv9_s.skill.MySkill")]
        [InlineData("copilotStudioSkill.d2df", "skills/copilotStudioSkill.d2df.mcs.yml", "cr5f7_agent6eFv9_s.skill.copilotStudioSkill.d2df")]
        public void SkillComponent_AutoSupported_RoundTrip(string shortName, string expectedPath, string expectedSchema)
        {
            // SkillComponent is convention-compliant: uses ObjectModel defaults
            // for infix/folder, but no LS-specific behavior override
            var projector = _registry.GetForType(typeof(SkillComponent)) as IComponentProjector;
            Assert.NotNull(projector);

            // Verify infix and folder from ObjectModel defaults
            Assert.Equal(".skill.", GetRuleInfix(projector!));
            Assert.Equal("skills/", projector!.Folder);

            // Create a component and verify path generation
            var component = new SkillComponent.Builder
            {
                SchemaName = expectedSchema,
                Skill = new SkillDefinition.Builder()
            }.Build();

            var context = new ProjectionContext { BotName = TestBotName };
            var actualPath = projector.GetFilePath(component, context);
            Assert.Equal(expectedPath, actualPath);

            // Verify round-trip: path → schema uses the short name from the path
            var pathWithoutExtension = actualPath.Replace(".mcs.yml", "");
            var derivedSchema = projector.GetSchemaName(pathWithoutExtension, TestBotName, projector.ElementType);
            Assert.Equal(expectedSchema, derivedSchema);

            // Verify short name is correctly derived
            Assert.Contains(shortName, expectedPath);
        }

        /// <summary>
        /// Verifies every concrete BotComponentBase type is covered by the LS projector registry and layout.
        /// This is an acceptance test for ObjectModel.dll updates.
        /// </summary>
        [Fact]
        public void AllBotComponentTypes_AreCovered_ByLsProjection()
        {
            var componentTypes = typeof(BotComponentBase).Assembly
                .GetTypes()
                .Where(t => t.IsClass && !t.IsAbstract)
                .Where(t => typeof(BotComponentBase).IsAssignableFrom(t))
                .Where(t => !t.Name.StartsWith("Unknown", StringComparison.Ordinal))
                .Where(t => t.Name != "LegacyOrUnknownComponent")
                .ToList();

            var missingProjectors = componentTypes
                .Where(t => _registry.GetForType(t) is not IComponentProjector)
                .Select(t => t.FullName)
                .ToList();

            Assert.True(missingProjectors.Count == 0, $"Missing LS projectors for: {string.Join(", ", missingProjectors)}");

            var missingLayout = componentTypes
                .Where(t => !HasFolderMapping(t))
                .Select(t => t.FullName)
                .ToList();

            Assert.True(missingLayout.Count == 0, $"Missing LS layout entries for: {string.Join(", ", missingLayout)}");
        }

        /// <summary>
        /// Verifies all DialogBase concrete types map to a known dialog projection group.
        /// This catches new dialog types that require LS routing updates.
        /// </summary>
        [Fact]
        public void AllDialogBaseTypes_MapToKnownDialogGroups()
        {
            var dialogTypes = typeof(DialogBase).Assembly
                .GetTypes()
                .Where(t => t.IsClass && !t.IsAbstract)
                .Where(t => typeof(DialogBase).IsAssignableFrom(t))
                .Where(t => !t.Name.StartsWith("Unknown", StringComparison.Ordinal))
                .ToList();

            var unmapped = dialogTypes
                .Where(t => !typeof(AdaptiveDialog).IsAssignableFrom(t)
                            && !typeof(TaskDialog).IsAssignableFrom(t)
                            && !typeof(AgentDialog).IsAssignableFrom(t))
                .Select(t => t.FullName)
                .ToList();

            Assert.True(unmapped.Count == 0, $"DialogBase types not mapped to topics/actions/agents: {string.Join(", ", unmapped)}");
        }

        /// <summary>
        /// Verifies that components with LS infix overrides are correctly identified.
        /// Note: With PluralForm in SolutionComponents.xml, ObjectModel now produces correct folder names,
        /// so overrides are primarily for infix differences (e.g., .GlobalVariableComponent. vs .globalvariable.).
        /// </summary>
        [Fact]
        public void OverriddenComponents_HaveRuleOverrides()
        {
            // These components HAVE legacy metadata overrides in LspProjection (at least for infix)
            var overriddenComponents = new[]
            {
                typeof(GlobalVariableComponent),
                typeof(BotSettingsComponent),
                typeof(CustomEntityComponent),
                typeof(ExternalTriggerComponent),
            };

            foreach (var componentType in overriddenComponents)
            {
                var projector = _registry.GetForType(componentType) as IComponentProjector;
                Assert.NotNull(projector);

                var ruleInfix = LspProjection.GetRuleInfixForElementType(projector!.ElementType);
                var ruleFolder = LspProjection.GetRuleFolderForElementType(projector.ElementType);

                Assert.False(string.IsNullOrEmpty(ruleInfix), $"{componentType.Name} should have a rule infix");
                Assert.False(string.IsNullOrEmpty(ruleFolder), $"{componentType.Name} should have a rule folder");

                // At minimum, infix should differ (e.g., .GlobalVariableComponent. vs .globalvariable.)
                Assert.NotEqual(projector.Infix, ruleInfix);
                // Folder may or may not differ depending on PluralForm usage
            }
        }

        #endregion

        private static string GetRuleInfix(IComponentProjector projector)
        {
            return LspProjection.GetRuleInfixForElementType(projector.ElementType) ?? projector.Infix;
        }

        private static bool HasFolderMapping(Type componentType)
        {
            if (componentType == typeof(DialogComponent))
            {
                return LspProjectionLayout.FileStructureMap.ContainsKey("topics/")
                    && LspProjectionLayout.FileStructureMap.ContainsKey("actions/")
                    && LspProjectionLayout.FileStructureMap.ContainsKey("agent");
            }

            if (componentType == typeof(GptComponent))
            {
                return LspProjectionLayout.FileStructureMap.ContainsKey("agent");
            }

            var projector = LspProjectorRegistry.Instance.GetForType(componentType) as IComponentProjector;
            if (projector == null)
            {
                return false;
            }

            var folder = LspProjection.GetRuleFolderForElementType(projector.ElementType) ?? projector.Folder;
            return LspProjectionLayout.FileStructureMap.ContainsKey(folder);
        }
    }
}

