namespace Microsoft.PowerPlatformLS.UnitTests.Contracts.FileLayout
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.CopilotStudio.McsCore;
    using Xunit;

    [Trait("Category", "Projection")]
    public class CliProjectionRuleTests
    {
        private const string Bot = "Default_draft_ECaOPZ";

        [Theory]
        [InlineData(typeof(InlineAgentSkill), "behaviors/weather", "Default_draft_ECaOPZ.skill.weather")]
        [InlineData(typeof(ConnectorTool), "capabilities/tools/Getsearchindexes", "Default_draft_ECaOPZ.tool.Getsearchindexes")]
        [InlineData(typeof(WorkflowTool), "capabilities/tools/AgentFlow1", "Default_draft_ECaOPZ.tool.AgentFlow1")]
        [InlineData(typeof(McpTool), "capabilities/tools/WorkIQCopilotPreview", "Default_draft_ECaOPZ.tool.WorkIQCopilotPreview")]
        [InlineData(typeof(ConnectedAgentTool), "capabilities/agents/cre98_AgentC4", "Default_draft_ECaOPZ.tool.connected-agent.cre98_AgentC4")]
        public void GetSchemaName_FromLocalPath_ProducesExpectedSchema(System.Type elementType, string pathWithoutExt, string expectedSchema)
        {
            var result = LspProjection.GetSchemaName(pathWithoutExt, Bot, elementType);
            Assert.Equal(expectedSchema, result);
        }

        [Theory]
        [InlineData(typeof(InlineAgentSkill), "Default_draft_ECaOPZ.skill.weather", "behaviors/weather.mcs.yml")]
        [InlineData(typeof(ConnectorTool), "Default_draft_ECaOPZ.tool.Getsearchindexes", "capabilities/tools/Getsearchindexes.mcs.yml")]
        [InlineData(typeof(WorkflowTool), "Default_draft_ECaOPZ.tool.AgentFlow1", "capabilities/tools/AgentFlow1.mcs.yml")]
        [InlineData(typeof(McpTool), "Default_draft_ECaOPZ.tool.WorkIQCopilotPreview", "capabilities/tools/WorkIQCopilotPreview.mcs.yml")]
        [InlineData(typeof(ConnectedAgentTool), "Default_draft_ECaOPZ.tool.connected-agent.cre98_AgentC4", "capabilities/agents/cre98_AgentC4.mcs.yml")]
        public void GetFilePath_FromSchema_ProducesExpectedLocalPath(System.Type elementType, string schema, string expectedPath)
        {
            var result = LspProjection.GetFilePath(elementType, schema, Bot, subAgentFolder: null, pathWithoutExtension: null);
            Assert.Equal(expectedPath, result);
        }

        [Theory]
        [InlineData(typeof(InlineAgentSkill), "Default_draft_ECaOPZ.skill.weather", "behaviors/Default_draft_ECaOPZ.skill.weather", "behaviors/Default_draft_ECaOPZ.skill.weather.mcs.yml")]
        [InlineData(typeof(ConnectorTool), "Default_draft_ECaOPZ.tool.Getsearchindexes", "capabilities/tools/Default_draft_ECaOPZ.tool.Getsearchindexes", "capabilities/tools/Default_draft_ECaOPZ.tool.Getsearchindexes.mcs.yml")]
        [InlineData(typeof(ConnectedAgentTool), "Default_draft_ECaOPZ.tool.connected-agent.cre98_AgentC4", "capabilities/agents/Default_draft_ECaOPZ.tool.connected-agent.cre98_AgentC4", "capabilities/agents/Default_draft_ECaOPZ.tool.connected-agent.cre98_AgentC4.mcs.yml")]
        public void GetFilePath_WithQualifiedPathContext_PreservesQualifiedFileName(System.Type elementType, string schema, string pathContext, string expectedPath)
        {
            var result = LspProjection.GetFilePath(elementType, schema, Bot, subAgentFolder: null, pathWithoutExtension: pathContext);
            Assert.Equal(expectedPath, result);
        }

        [Fact]
        public void NoCliRule_PointsAt_TranslationsFolder()
        {
            System.Type[] cliTypes = { typeof(InlineAgentSkill), typeof(ConnectorTool), typeof(WorkflowTool), typeof(McpTool), typeof(ConnectedAgentTool) };
            foreach (var t in cliTypes)
            {
                var folder = LspProjection.GetRuleFolderForElementType(t);
                Assert.NotNull(folder);
                Assert.False(folder!.StartsWith("translations", System.StringComparison.OrdinalIgnoreCase),
                    $"Rule for {t.Name} should not project to translations/.");
            }
        }

        [Fact]
        public void CliKnowledge_BotPrefixedFile_PreservesSchemaName()
        {
            const string fileName = "Default_draft_ECaOPZ.Book2xlsx_TZ6t5Yt3Ir8ScTHFgs979";
            var schema = LspProjection.GetSchemaName("knowledge/" + fileName, Bot, typeof(KnowledgeSourceConfiguration));
            Assert.Equal(fileName, schema);

            var path = LspProjection.GetFilePath(typeof(KnowledgeSourceConfiguration), schema!, Bot, subAgentFolder: null, pathWithoutExtension: "knowledge/" + fileName);
            Assert.Equal("knowledge/" + fileName + ".mcs.yml", path);
        }

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
