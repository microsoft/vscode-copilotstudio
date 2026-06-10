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
        [InlineData(typeof(ConnectorTool), "capabilities/tools/Getsearchindexes", "Default_draft_ECaOPZ.tool.Getsearchindexes")]
        [InlineData(typeof(WorkflowTool), "capabilities/tools/AgentFlow1", "Default_draft_ECaOPZ.tool.AgentFlow1")]
        [InlineData(typeof(McpTool), "capabilities/tools/WorkIQCopilotPreview", "Default_draft_ECaOPZ.tool.WorkIQCopilotPreview")]
        [InlineData(typeof(ConnectedAgentTool), "capabilities/tools/cre98_AgentC4", "Default_draft_ECaOPZ.tool.connected-agent.cre98_AgentC4")]
        public void GetSchemaName_FromLocalPath_ProducesExpectedSchema(System.Type elementType, string pathWithoutExt, string expectedSchema)
        {
            var result = LspProjection.GetSchemaName(pathWithoutExt, Bot, elementType, Cli);
            Assert.Equal(expectedSchema, result);
        }

        [Theory]
        [InlineData(typeof(InlineAgentSkill), "Default_draft_ECaOPZ.skill.weather", "behaviors/weather.mcs.yml")]
        [InlineData(typeof(ConnectorTool), "Default_draft_ECaOPZ.tool.Getsearchindexes", "capabilities/tools/Getsearchindexes.mcs.yml")]
        [InlineData(typeof(WorkflowTool), "Default_draft_ECaOPZ.tool.AgentFlow1", "capabilities/tools/AgentFlow1.mcs.yml")]
        [InlineData(typeof(McpTool), "Default_draft_ECaOPZ.tool.WorkIQCopilotPreview", "capabilities/tools/WorkIQCopilotPreview.mcs.yml")]
        [InlineData(typeof(ConnectedAgentTool), "Default_draft_ECaOPZ.tool.connected-agent.cre98_AgentC4", "capabilities/tools/cre98_AgentC4.mcs.yml")]
        public void GetFilePath_FromSchema_ProducesExpectedLocalPath(System.Type elementType, string schema, string expectedPath)
        {
            var result = LspProjection.GetFilePath(elementType, schema, Bot, subAgentFolder: null, pathWithoutExtension: null, Cli);
            Assert.Equal(expectedPath, result);
        }

        [Theory]
        [InlineData(typeof(InlineAgentSkill), "Default_draft_ECaOPZ.skill.weather", "behaviors/Default_draft_ECaOPZ.skill.weather", "behaviors/Default_draft_ECaOPZ.skill.weather.mcs.yml")]
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
            System.Type[] cliTypes = { typeof(InlineAgentSkill), typeof(ConnectorTool), typeof(WorkflowTool), typeof(McpTool), typeof(ConnectedAgentTool) };
            foreach (var t in cliTypes)
            {
                var folder = LspProjection.GetRuleFolderForElementType(t, Cli);
                Assert.NotNull(folder);
                Assert.False(folder!.StartsWith("translations", System.StringComparison.OrdinalIgnoreCase),
                    $"CLI rule for {t.Name} should not project to translations/.");
            }
        }

        [Fact]
        public void ConnectedAgentTool_RoutesToCapabilitiesTools_NotAgents()
        {
            var folder = LspProjection.GetRuleFolderForElementType(typeof(ConnectedAgentTool), Cli);
            Assert.Equal("capabilities/tools/", folder);
        }

        // CLI shared types (knowledge + file attachments) project to the three-layer
        // capabilities/knowledge[/files]/ folders under CliCopilot (D21).

        [Theory]
        [InlineData(typeof(KnowledgeSourceConfiguration), "capabilities/knowledge/Weather", "Default_draft_ECaOPZ.knowledge.Weather")]
        [InlineData(typeof(FileAttachmentComponent), "capabilities/knowledge/files/MyLedger.xlsx_hyG", "Default_draft_ECaOPZ.file.MyLedger.xlsx_hyG")]
        public void GetSchemaName_CliSharedType_ProducesExpectedSchema(System.Type elementType, string pathWithoutExt, string expectedSchema)
        {
            var result = LspProjection.GetSchemaName(pathWithoutExt, Bot, elementType, Cli);
            Assert.Equal(expectedSchema, result);
        }

        [Theory]
        [InlineData(typeof(KnowledgeSourceConfiguration), "Default_draft_ECaOPZ.knowledge.Weather", "capabilities/knowledge/Weather.mcs.yml")]
        [InlineData(typeof(FileAttachmentComponent), "Default_draft_ECaOPZ.file.MyLedger.xlsx_hyG", "capabilities/knowledge/files/MyLedger.xlsx_hyG.mcs.yml")]
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
