namespace Microsoft.PowerPlatformLS.UnitTests.Contracts.FileLayout
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.CopilotStudio.McsCore;
    using Xunit;

    /// <summary>
    /// CLI three-layer projection rule coverage, recovered from PR #265 and adapted
    /// to the shape-gated <c>CliRules</c> (TDD D20): CLI types are projected under an
    /// explicit <see cref="AuthoringShape.CliCopilot"/> shape, and connected agents
    /// route to <c>capabilities/tools/</c> (D10), not <c>capabilities/agents/</c>.
    /// </summary>
    [Trait("Category", "Projection")]
    public class CliProjectionRuleTests
    {
        private const string Bot = "Default_draft_ECaOPZ";
        private const AuthoringShape Cli = AuthoringShape.CliCopilot;

        [Theory]
        [InlineData(typeof(InlineAgentSkill), "behaviors/weather", "Default_draft_ECaOPZ.skill.weather")]
        [InlineData(typeof(InlineAgentSkill), "behaviors/weather/skill", "Default_draft_ECaOPZ.skill.weather")]
        [InlineData(typeof(ImportedPackageSkill), "behaviors/weather", "Default_draft_ECaOPZ.skill.weather")]
        [InlineData(typeof(ConnectorTool), "capabilities/tools/Getsearchindexes", "Default_draft_ECaOPZ.tool.Getsearchindexes")]
        [InlineData(typeof(WorkflowTool), "capabilities/tools/AgentFlow1", "Default_draft_ECaOPZ.tool.AgentFlow1")]
        [InlineData(typeof(McpTool), "capabilities/tools/WorkIQCopilotPreview", "Default_draft_ECaOPZ.tool.WorkIQCopilotPreview")]
        [InlineData(typeof(FabricTool), "capabilities/tools/Fabric1", "Default_draft_ECaOPZ.tool.Fabric1")]
        [InlineData(typeof(PackagedMcpTool), "capabilities/tools/Packaged1", "Default_draft_ECaOPZ.tool.Packaged1")]
        [InlineData(typeof(BrokeredMcpTool), "capabilities/tools/Brokered1", "Default_draft_ECaOPZ.tool.Brokered1")]
        [InlineData(typeof(IQCapability), "capabilities/tools/Iq1", "Default_draft_ECaOPZ.tool.Iq1")]
        [InlineData(typeof(ConnectedAgentTool), "capabilities/tools/cre98_AgentC4", "Default_draft_ECaOPZ.tool.connected-agent.cre98_AgentC4")]
        [InlineData(typeof(AgentToAgentTool), "capabilities/tools/cre98_AgentC5", "Default_draft_ECaOPZ.tool.connected-agent.cre98_AgentC5")]
        [InlineData(typeof(SDKAgentTool), "capabilities/tools/cre98_AgentC6", "Default_draft_ECaOPZ.tool.connected-agent.cre98_AgentC6")]
        [InlineData(typeof(FoundryAgentTool), "capabilities/tools/cre98_AgentC7", "Default_draft_ECaOPZ.tool.connected-agent.cre98_AgentC7")]
        [InlineData(typeof(AgentHook), "hooks/Onsessionstart_aLf", "Default_draft_ECaOPZ.tool.Onsessionstart_aLf")]
        public void GetSchemaName_FromLocalPath_ProducesExpectedSchema(System.Type elementType, string pathWithoutExt, string expectedSchema)
        {
            var result = LspProjection.GetSchemaName(pathWithoutExt, Bot, elementType, Cli);
            Assert.Equal(expectedSchema, result);
        }

        [Theory]
        [InlineData(typeof(InlineAgentSkill), "Default_draft_ECaOPZ.skill.weather", "behaviors/weather/skill.mcs.yml")]
        [InlineData(typeof(ImportedPackageSkill), "Default_draft_ECaOPZ.skill.weather", "behaviors/weather.mcs.yml")]
        [InlineData(typeof(ConnectorTool), "Default_draft_ECaOPZ.tool.Getsearchindexes", "capabilities/tools/Getsearchindexes.mcs.yml")]
        [InlineData(typeof(WorkflowTool), "Default_draft_ECaOPZ.tool.AgentFlow1", "capabilities/tools/AgentFlow1.mcs.yml")]
        [InlineData(typeof(McpTool), "Default_draft_ECaOPZ.tool.WorkIQCopilotPreview", "capabilities/tools/WorkIQCopilotPreview.mcs.yml")]
        [InlineData(typeof(FabricTool), "Default_draft_ECaOPZ.tool.Fabric1", "capabilities/tools/Fabric1.mcs.yml")]
        [InlineData(typeof(PackagedMcpTool), "Default_draft_ECaOPZ.tool.Packaged1", "capabilities/tools/Packaged1.mcs.yml")]
        [InlineData(typeof(BrokeredMcpTool), "Default_draft_ECaOPZ.tool.Brokered1", "capabilities/tools/Brokered1.mcs.yml")]
        [InlineData(typeof(IQCapability), "Default_draft_ECaOPZ.tool.Iq1", "capabilities/tools/Iq1.mcs.yml")]
        [InlineData(typeof(ConnectedAgentTool), "Default_draft_ECaOPZ.tool.connected-agent.cre98_AgentC4", "capabilities/tools/cre98_AgentC4.mcs.yml")]
        [InlineData(typeof(AgentToAgentTool), "Default_draft_ECaOPZ.tool.connected-agent.cre98_AgentC5", "capabilities/tools/cre98_AgentC5.mcs.yml")]
        [InlineData(typeof(SDKAgentTool), "Default_draft_ECaOPZ.tool.connected-agent.cre98_AgentC6", "capabilities/tools/cre98_AgentC6.mcs.yml")]
        [InlineData(typeof(FoundryAgentTool), "Default_draft_ECaOPZ.tool.connected-agent.cre98_AgentC7", "capabilities/tools/cre98_AgentC7.mcs.yml")]
        [InlineData(typeof(AgentHook), "Default_draft_ECaOPZ.tool.Onsessionstart_aLf", "hooks/Onsessionstart_aLf.mcs.yml")]
        public void GetFilePath_FromSchema_ProducesExpectedLocalPath(System.Type elementType, string schema, string expectedPath)
        {
            var result = LspProjection.GetFilePath(elementType, schema, Bot, subAgentFolder: null, pathWithoutExtension: null, Cli);
            Assert.Equal(expectedPath, result);
        }

        [Theory]
        [InlineData(typeof(ConnectorTool), "Default_draft_ECaOPZ.tool.Getsearchindexes", "capabilities/tools/Default_draft_ECaOPZ.tool.Getsearchindexes", "capabilities/tools/Default_draft_ECaOPZ.tool.Getsearchindexes.mcs.yml")]
        [InlineData(typeof(ConnectedAgentTool), "Default_draft_ECaOPZ.tool.connected-agent.cre98_AgentC4", "capabilities/tools/Default_draft_ECaOPZ.tool.connected-agent.cre98_AgentC4", "capabilities/tools/Default_draft_ECaOPZ.tool.connected-agent.cre98_AgentC4.mcs.yml")]
        public void GetFilePath_WithQualifiedPathContext_PreservesQualifiedFileName(System.Type elementType, string schema, string pathContext, string expectedPath)
        {
            var result = LspProjection.GetFilePath(elementType, schema, Bot, subAgentFolder: null, pathWithoutExtension: pathContext, Cli);
            Assert.Equal(expectedPath, result);
        }

        [Fact]
        public void CliRule_DoesNotPointAt_TranslationsFolder()
        {
            var dialogTypes = typeof(DialogBase).Assembly
                .GetTypes()
                .Where(t => t.IsClass && !t.IsAbstract)
                .Where(t => typeof(DialogBase).IsAssignableFrom(t))
                .Where(t => !t.Name.StartsWith("Unknown", System.StringComparison.Ordinal));

            foreach (var t in dialogTypes)
            {
                var folder = LspProjection.GetRuleFolderForElementType(t, Cli);
                Assert.NotNull(folder);
                Assert.False(folder!.StartsWith("translations", System.StringComparison.OrdinalIgnoreCase),
                    $"CLI rule for {t.Name} should not project to translations/.");
            }
        }

        [Theory]
        [InlineData(typeof(ImportedPackageSkill), "behaviors/", ".skill.")]
        [InlineData(typeof(FabricTool), "capabilities/tools/", ".tool.")]
        [InlineData(typeof(PackagedMcpTool), "capabilities/tools/", ".tool.")]
        [InlineData(typeof(BrokeredMcpTool), "capabilities/tools/", ".tool.")]
        [InlineData(typeof(IQCapability), "capabilities/tools/", ".tool.")]
        [InlineData(typeof(AgentToAgentTool), "capabilities/tools/", ".tool.connected-agent.")]
        [InlineData(typeof(SDKAgentTool), "capabilities/tools/", ".tool.connected-agent.")]
        [InlineData(typeof(FoundryAgentTool), "capabilities/tools/", ".tool.connected-agent.")]
        public void ToolFamilyTypes_InheritSiblingRule_WithoutPerTypeRegistration(System.Type elementType, string expectedFolder, string expectedInfix)
        {
            Assert.False(LspProjection.CliRules.ContainsKey(elementType));
            Assert.Equal(expectedFolder, LspProjection.GetRuleFolderForElementType(elementType, Cli));
            Assert.Equal(expectedInfix, LspProjection.GetRuleInfixForElementType(elementType, Cli));
        }

        [Fact]
        public void AgentHook_RoutesToHooks_UsingToolInfix()
        {
            const string hookBot = "crf9a_nb2_4tl6mu";
            const string hookSchema = "crf9a_nb2_4tl6mu.tool.Onsessionstart_aLf";

            Assert.Equal(LspProjection.HooksFolder, LspProjection.GetRuleFolderForElementType(typeof(AgentHook), Cli));

            var path = LspProjection.GetFilePath(
                typeof(AgentHook), hookSchema, hookBot, subAgentFolder: null, pathWithoutExtension: null, Cli);
            Assert.Equal("hooks/Onsessionstart_aLf.mcs.yml", path);

            var schema = LspProjection.GetSchemaName("hooks/Onsessionstart_aLf", hookBot, typeof(AgentHook), Cli);
            Assert.Equal(hookSchema, schema);
        }

        [Fact]
        public void AgentHook_RoutesToHooks_InEveryShape()
        {
            Assert.Equal(LspProjection.HooksFolder, LspProjection.GetRuleFolderForElementType(typeof(AgentHook), Cli));
            Assert.Equal(LspProjection.HooksFolder, LspProjection.GetRuleFolderForElementType(typeof(AgentHook), AuthoringShape.Classic));
        }

        [Fact]
        public void ConnectedAgentTool_RoutesToCapabilitiesTools_NotAgents()
        {
            var folder = LspProjection.GetRuleFolderForElementType(typeof(ConnectedAgentTool), Cli);
            Assert.Equal("capabilities/tools/", folder);
        }

        /// <summary>
        /// Real cloud shape: the server names a connected-agent tool
        /// <c>{bot}.tool.connected-agent.{bot}.action.{name}</c> - the segment after the canonical
        /// infix is itself a fully-qualified schema name. Stripping only the infix leaves a leaf that
        /// still repeats the whole bot prefix, which is what produced the long file names (and, for a
        /// long bot name, a derived SchemaName over the 100-character limit).
        /// </summary>
        /// <remarks>
        /// The nested and simple shapes reduce to the same short leaf, so the file name alone cannot
        /// tell them apart. Reading resolves the simple shape (see
        /// <see cref="SimpleToolSchema_RoundTripsExactly"/>); an existing component of either shape
        /// keeps its real identity because callers correlate the file to its cached schema first. What
        /// must hold here is that the leaf stays short, since the long form is what overflowed the
        /// schema-name limit.
        /// </remarks>
        [Theory]
        [InlineData(typeof(ConnectedAgentTool), "Default_draft_ECaOPZ.tool.connected-agent.Default_draft_ECaOPZ.action.cre98_AgentC4_UqzqxqiJ")]
        [InlineData(typeof(ConnectorTool), "Default_draft_ECaOPZ.tool.Default_draft_ECaOPZ.action.SendEmail_ab12")]
        [InlineData(typeof(WorkflowTool), "Default_draft_ECaOPZ.tool.Default_draft_ECaOPZ.action.AgentFlow1_x9")]
        [InlineData(typeof(McpTool), "Default_draft_ECaOPZ.tool.Default_draft_ECaOPZ.action.WorkIQ_x9")]
        public void NestedQualifiedToolSchema_SharesTheSimpleLeaf(System.Type elementType, string schema)
        {
            var path = LspProjection.GetFilePath(elementType, schema, Bot, subAgentFolder: null, pathWithoutExtension: null, Cli);
            var recovered = LspProjection.GetSchemaName(
                path!.Substring(0, path.Length - ".mcs.yml".Length), Bot, elementType, Cli);

            Assert.DoesNotContain(Bot, System.IO.Path.GetFileName(path!), System.StringComparison.Ordinal);
            Assert.Equal(LspProjection.GetFilePath(elementType, recovered!, Bot, subAgentFolder: null, pathWithoutExtension: null, Cli), path);
        }

        [Theory]
        [InlineData(typeof(ConnectedAgentTool), "Default_draft_ECaOPZ.action.cre98_AgentC4_UqzqxqiJ", "capabilities/tools/action.cre98_AgentC4_UqzqxqiJ.mcs.yml")]
        [InlineData(typeof(ConnectorTool), "Default_draft_ECaOPZ.action.SendEmail_ab12", "capabilities/tools/action.SendEmail_ab12.mcs.yml")]
        [InlineData(typeof(WorkflowTool), "Default_draft_ECaOPZ.action.AgentFlow1_x9", "capabilities/tools/action.AgentFlow1_x9.mcs.yml")]
        [InlineData(typeof(McpTool), "Default_draft_ECaOPZ.action.WorkIQ_x9", "capabilities/tools/action.WorkIQ_x9.mcs.yml")]
        public void SimpleToolSchema_RoundTripsExactly(System.Type elementType, string schema, string expectedPath)
        {
            var path = LspProjection.GetFilePath(elementType, schema, Bot, subAgentFolder: null, pathWithoutExtension: null, Cli);
            Assert.Equal(expectedPath, path);

            var recovered = LspProjection.GetSchemaName(
                path!.Substring(0, path.Length - ".mcs.yml".Length), Bot, elementType, Cli);
            Assert.Equal(schema, recovered);
        }

        /// <summary>
        /// Without a guard this would be written as <c>action.Bar</c> and read back as the simple
        /// <c>{bot}.action.Bar</c>, silently renaming the component. It falls back to the qualified name.
        /// </summary>
        [Theory]
        [InlineData(typeof(ConnectedAgentTool), "Default_draft_ECaOPZ.tool.connected-agent.action.Bar")]
        [InlineData(typeof(ConnectorTool), "Default_draft_ECaOPZ.tool.action.Bar")]
        public void CanonicalToolSchema_WhoseShortNameLooksLikeALeaf_RoundTripsExactly(System.Type elementType, string schema)
        {
            var path = LspProjection.GetFilePath(elementType, schema, Bot, subAgentFolder: null, pathWithoutExtension: null, Cli);
            var recovered = LspProjection.GetSchemaName(
                path!.Substring(0, path.Length - ".mcs.yml".Length), Bot, elementType, Cli);

            Assert.Equal(schema, recovered);
        }

        /// <summary>
        /// Reported repro: the derived schema previously re-expanded to 164 characters and tripped the
        /// PropertyLengthTooLong diagnostic right after clone.
        /// </summary>
        [Fact]
        public void SimpleConnectedAgent_LongBotName_StaysUnderSchemaNameLimit()
        {
            const string bot = "hardware_IGTS_DFMEA_superskilled_agent2.1_t4RnpS";
            const string schema = "hardware_IGTS_DFMEA_superskilled_agent2.1_t4RnpS.action.cr5ab_endeffect2_rmmv4_OPyYm1_UqzqxqiJ";

            var path = LspProjection.GetFilePath(typeof(ConnectedAgentTool), schema, bot, subAgentFolder: null, pathWithoutExtension: null, Cli);
            Assert.Equal("capabilities/tools/action.cr5ab_endeffect2_rmmv4_OPyYm1_UqzqxqiJ.mcs.yml", path);

            var recovered = LspProjection.GetSchemaName(
                path!.Substring(0, path.Length - ".mcs.yml".Length), bot, typeof(ConnectedAgentTool), Cli);
            Assert.Equal(schema, recovered);
            Assert.True(recovered!.Length <= 100);
        }

        [Fact]
        public void PlainToolSchema_WithoutNestedQualifier_KeepsBareShortName()
        {
            const string schema = "Default_draft_ECaOPZ.tool.connected-agent.cre98_AgentC4";

            var path = LspProjection.GetFilePath(typeof(ConnectedAgentTool), schema, Bot, subAgentFolder: null, pathWithoutExtension: null, Cli);
            Assert.Equal("capabilities/tools/cre98_AgentC4.mcs.yml", path);

            var recovered = LspProjection.GetSchemaName(
                path!.Substring(0, path.Length - ".mcs.yml".Length), Bot, typeof(ConnectedAgentTool), Cli);
            Assert.Equal(schema, recovered);
        }

        [Fact]
        public void PortalAuthoredTool_KeepsClassicProjection_ForNonCliShapes()
        {
            const string schema = "Default_draft_ECaOPZ.action.SendEmail_ab12";

            var classic = LspProjection.GetFilePath(typeof(TaskDialog), schema, Bot, subAgentFolder: null, pathWithoutExtension: null);

            Assert.Equal("actions/SendEmail_ab12.mcs.yml", classic);
        }

        // CLI shared types (knowledge + file attachments) project to the three-layer
        // capabilities/knowledge[/files]/ folders under CliCopilot (D21).

        [Theory]
        [InlineData(typeof(KnowledgeSourceConfiguration), "capabilities/knowledge/Weather", "Default_draft_ECaOPZ.knowledge.Weather")]
        [InlineData(typeof(FileAttachmentComponent), "capabilities/knowledge/files/MyLedger.xlsx_hyG", "Default_draft_ECaOPZ.file.MyLedger.xlsx_hyG")]
        [InlineData(typeof(FileAttachmentComponent), "capabilities/knowledge/files/file22txt_dBKwq", "Default_draft_ECaOPZ.file.file22txt_dBKwq")]
        public void GetSchemaName_CliSharedType_ProducesExpectedSchema(System.Type elementType, string pathWithoutExt, string expectedSchema)
        {
            var result = LspProjection.GetSchemaName(pathWithoutExt, Bot, elementType, Cli);
            Assert.Equal(expectedSchema, result);
        }

        [Theory]
        [InlineData(typeof(KnowledgeSourceConfiguration), "Default_draft_ECaOPZ.knowledge.Weather", "capabilities/knowledge/Weather.mcs.yml")]
        [InlineData(typeof(FileAttachmentComponent), "Default_draft_ECaOPZ.file.MyLedger.xlsx_hyG", "capabilities/knowledge/files/MyLedger.xlsx_hyG.mcs.yml")]
        [InlineData(typeof(FileAttachmentComponent), "Default_draft_ECaOPZ.file.file22txt_dBKwq", "capabilities/knowledge/files/file22txt_dBKwq.mcs.yml")]
        public void GetFilePath_CliSharedType_ProducesExpectedPath(System.Type elementType, string schema, string expectedPath)
        {
            var result = LspProjection.GetFilePath(elementType, schema, Bot, subAgentFolder: null, pathWithoutExtension: null, Cli);
            Assert.Equal(expectedPath, result);
        }

        [Fact]
        public void Knowledge_RoutesByShape_ClassicVsCliCopilot()
        {
            const string schema = "Default_draft_ECaOPZ.knowledge.Weather";

            var classic = LspProjection.GetFilePath(typeof(KnowledgeSourceConfiguration), schema, Bot, subAgentFolder: null, pathWithoutExtension: null);
            var cli = LspProjection.GetFilePath(typeof(KnowledgeSourceConfiguration), schema, Bot, subAgentFolder: null, pathWithoutExtension: null, Cli);

            Assert.Equal("knowledge/Weather.mcs.yml", classic);
            Assert.Equal("capabilities/knowledge/Weather.mcs.yml", cli);
        }

        [Fact]
        public void CliKnowledge_BotPrefixedFile_PreservesSchemaName_UnderCliCopilot()
        {
            const string fileName = "Default_draft_ECaOPZ.Book2xlsx_TZ6t5Yt3Ir8ScTHFgs979";
            var pathContext = "capabilities/knowledge/" + fileName;

            var schema = LspProjection.GetSchemaName(pathContext, Bot, typeof(KnowledgeSourceConfiguration), Cli);
            Assert.Equal(fileName, schema);

            var path = LspProjection.GetFilePath(typeof(KnowledgeSourceConfiguration), schema!, Bot, subAgentFolder: null, pathWithoutExtension: pathContext, Cli);
            Assert.Equal(pathContext + ".mcs.yml", path);
        }

        // The following assert classic knowledge behavior is regression-safe (CLI
        // knowledge stays at knowledge/ for the classic shape). They use the default
        // (classic) shape and the classic knowledge/ folder.

        [Fact]
        public void ClassicKnowledge_QualifiedFile_StillPreserves()
        {
            const string fileName = "Default_draft_ECaOPZ.knowledge.MyKnowledge";
            var schema = LspProjection.GetSchemaName("knowledge/" + fileName, Bot, typeof(KnowledgeSourceConfiguration));
            Assert.Equal(fileName, schema);
        }

        [Fact]
        public void ClassicKnowledge_DottedDisplayName_StillExpands()
        {
            var schema = LspProjection.GetSchemaName("knowledge/PublicSiteSearchSource.0", "agent1", typeof(KnowledgeSourceConfiguration));
            Assert.Equal("agent1.knowledge.PublicSiteSearchSource.0", schema);
        }

        [Fact]
        public void ClassicKnowledge_BotPrefixedThreeSegment_StillExpands()
        {
            var schema = LspProjection.GetSchemaName("knowledge/agent1.component.Custom", "agent1", typeof(KnowledgeSourceConfiguration));
            Assert.Equal("agent1.knowledge.agent1.component.Custom", schema);
        }
    }
}
