namespace Microsoft.PowerPlatformLS.UnitTests.Contracts.FileLayout
{
    using System;
    using System.Linq;
    using Microsoft.Agents.ObjectModel;
    using Microsoft.CopilotStudio.McsCore;
    using Xunit;

    /// <summary>
    /// Classic-shape projection for the <see cref="AgentToolBase"/> family and
    /// <see cref="AgentHook"/>: tools route to tools/, connected agents to agents/,
    /// skills to skills/ and hooks to hooks/, instead of inheriting the
    /// LocalizableContentContainer translations/ rule.
    /// </summary>
    [Trait("Category", "Projection")]
    public class ClassicToolProjectionTests
    {
        private const string Bot = "agent1";
        private const AuthoringShape Classic = AuthoringShape.Classic;

        [Theory]
        [InlineData(typeof(ConnectorTool), "tools/", ".tool.")]
        [InlineData(typeof(McpTool), "tools/", ".tool.")]
        [InlineData(typeof(PackagedMcpTool), "tools/", ".tool.")]
        [InlineData(typeof(BrokeredMcpTool), "tools/", ".tool.")]
        [InlineData(typeof(FabricTool), "tools/", ".tool.")]
        [InlineData(typeof(WorkflowTool), "tools/", ".tool.")]
        [InlineData(typeof(IQCapability), "tools/", ".tool.")]
        [InlineData(typeof(ConnectedAgentTool), "agents/", ".tool.connected-agent.")]
        [InlineData(typeof(AgentToAgentTool), "agents/", ".tool.connected-agent.")]
        [InlineData(typeof(SDKAgentTool), "agents/", ".tool.connected-agent.")]
        [InlineData(typeof(FoundryAgentTool), "agents/", ".tool.connected-agent.")]
        [InlineData(typeof(InlineAgentSkill), "skills/", ".skill.")]
        [InlineData(typeof(ImportedPackageSkill), "skills/", ".skill.")]
        [InlineData(typeof(AgentHook), "hooks/", ".tool.")]
        public void ClassicRule_RoutesToExpectedFolderAndInfix(Type elementType, string expectedFolder, string expectedInfix)
        {
            Assert.Equal(expectedFolder, LspProjection.GetRuleFolderForElementType(elementType, Classic));
            Assert.Equal(expectedInfix, LspProjection.GetRuleInfixForElementType(elementType, Classic));
        }

        [Theory]
        [InlineData(typeof(ConnectorTool), "agent1.tool.Search", "tools/Search.mcs.yml")]
        [InlineData(typeof(McpTool), "agent1.tool.Search", "tools/Search.mcs.yml")]
        [InlineData(typeof(AgentHook), "agent1.tool.Onsessionstart_aLf", "hooks/Onsessionstart_aLf.mcs.yml")]
        [InlineData(typeof(ConnectedAgentTool), "agent1.tool.connected-agent.AgentC4", "agents/AgentC4.mcs.yml")]
        [InlineData(typeof(ImportedPackageSkill), "agent1.skill.weather", "skills/weather.mcs.yml")]
        public void ClassicProjection_RoundTripsSchemaAndPath(Type elementType, string schema, string expectedPath)
        {
            var path = LspProjection.GetFilePath(elementType, schema, Bot, subAgentFolder: null, pathWithoutExtension: null, Classic);
            Assert.Equal(expectedPath, path);

            var recovered = LspProjection.GetSchemaName(
                path!.Substring(0, path.Length - ".mcs.yml".Length), Bot, elementType, Classic);
            Assert.Equal(schema, recovered);
        }

        [Fact]
        public void ClassicRule_NeverProjectsDialogsToTranslations()
        {
            var dialogTypes = typeof(DialogBase).Assembly
                .GetTypes()
                .Where(t => t.IsClass && !t.IsAbstract)
                .Where(t => typeof(DialogBase).IsAssignableFrom(t))
                .Where(t => !t.Name.StartsWith("Unknown", StringComparison.Ordinal));

            foreach (var type in dialogTypes)
            {
                var folder = LspProjection.GetRuleFolderForElementType(type, Classic);
                Assert.NotNull(folder);
                Assert.False(folder!.StartsWith("translations", StringComparison.OrdinalIgnoreCase),
                    $"Classic rule for {type.Name} should not project to translations/.");
            }
        }

        [Fact]
        public void ClassicDialogRules_DoNotRegressExistingFolders()
        {
            Assert.Equal("topics/", LspProjection.GetRuleFolderForElementType(typeof(AdaptiveDialog), Classic));
            Assert.Equal("actions/", LspProjection.GetRuleFolderForElementType(typeof(TaskDialog), Classic));
            Assert.Equal("agents/", LspProjection.GetRuleFolderForElementType(typeof(AgentDialog), Classic));
        }

        [Fact]
        public void ClassicAndCli_DivergeOnFolder_ButShareSchemaInfix()
        {
            Type[] toolTypes = { typeof(ConnectorTool), typeof(McpTool), typeof(WorkflowTool) };

            foreach (var type in toolTypes)
            {
                Assert.Equal("tools/", LspProjection.GetRuleFolderForElementType(type, Classic));
                Assert.Equal("capabilities/tools/", LspProjection.GetRuleFolderForElementType(type, AuthoringShape.CliCopilot));
                Assert.Equal(
                    LspProjection.GetRuleInfixForElementType(type, Classic),
                    LspProjection.GetRuleInfixForElementType(type, AuthoringShape.CliCopilot));
            }
        }
    }
}
