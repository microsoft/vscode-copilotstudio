namespace Microsoft.PowerPlatformLS.UnitTests.Contracts.FileLayout
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.CopilotStudio.McsCore;
    using System;
    using System.Linq;
    using Xunit;

    /// <summary>
    /// Verifies the authoring-shape GATE in <see cref="LspProjection"/> (TDD D20):
    /// CLI rules apply only under <see cref="AuthoringShape.CliCopilot"/>, while
    /// classic and unknown shapes use the unchanged classic rules. The full CLI rule
    /// correctness/round-trip coverage lives in <c>CliProjectionRuleTests</c>; this
    /// suite focuses on the classic-vs-CliCopilot divergence.
    /// </summary>
    [Trait("Category", "Projection")]
    public class CliProjectionShapeTests
    {
        private const string BotName = "agent1";

        [Fact]
        public void CliTool_OnlyRoutesToThreeLayer_UnderCliCopilot()
        {
            const string schema = "agent1.tool.Search";

            var cli = LspProjection.GetFilePath(
                typeof(ConnectorTool), schema, BotName, subAgentFolder: null,
                pathWithoutExtension: "capabilities/tools/Search", AuthoringShape.CliCopilot);

            // Classic resolves ConnectorTool via type assignability to a non-CLI route
            // (it would misroute without the shape gate); the CLI three-layer route is
            // reached only under CliCopilot.
            var classic = LspProjection.GetFilePath(
                typeof(ConnectorTool), schema, BotName, subAgentFolder: null,
                pathWithoutExtension: "capabilities/tools/Search", AuthoringShape.Classic);

            Assert.Equal("capabilities/tools/Search.mcs.yml", cli);
            Assert.NotEqual(cli, classic);
        }

        [Fact]
        public void ConnectedAgent_RoutesToCapabilitiesTools_NotAgents_UnderCliCopilot()
        {
            const string schema = "agent1.tool.connected-agent.cre98_AgentC4";

            var path = LspProjection.GetFilePath(
                typeof(ConnectedAgentTool), schema, BotName, subAgentFolder: null,
                pathWithoutExtension: null, AuthoringShape.CliCopilot);

            // D10: connected agents go to capabilities/tools/, not capabilities/agents/.
            Assert.Equal("capabilities/tools/cre98_AgentC4.mcs.yml", path);
        }

        [Fact]
        public void CliTool_DefaultAndUnknownShapes_MatchClassic()
        {
            const string schema = "agent1.tool.Search";

            var classic = LspProjection.GetFilePath(
                typeof(ConnectorTool), schema, BotName, null, "capabilities/tools/Search", AuthoringShape.Classic);
            var unknown = LspProjection.GetFilePath(
                typeof(ConnectorTool), schema, BotName, null, "capabilities/tools/Search", AuthoringShape.Unknown);
            var defaulted = LspProjection.GetFilePath(
                typeof(ConnectorTool), schema, BotName, null, "capabilities/tools/Search");

            Assert.Equal(classic, unknown);
            Assert.Equal(classic, defaulted);
            // None of the non-CLI shapes reach the CLI three-layer route.
            Assert.NotEqual("capabilities/tools/Search.mcs.yml", classic);
        }

        [Theory]
        [InlineData(AuthoringShape.Classic)]
        [InlineData(AuthoringShape.CliCopilot)]
        [InlineData(AuthoringShape.Unknown)]
        public void ClassicType_IsShapeInvariant(AuthoringShape shape)
        {
            // AdaptiveDialog has no CLI override, so every shape resolves it via the
            // shared classic Rules (topics/).
            var path = LspProjection.GetFilePath(
                typeof(AdaptiveDialog), "agent1.topic.Greeting", BotName, subAgentFolder: null,
                pathWithoutExtension: "topics/Greeting", shape);

            Assert.Equal("topics/Greeting.mcs.yml", path);
        }

        [Fact]
        public void CliSkill_SchemaName_RoundTrips_UnderCliCopilot()
        {
            var schema = LspProjection.GetSchemaName(
                "behaviors/weather", BotName, typeof(InlineAgentSkill), AuthoringShape.CliCopilot);

            Assert.Equal("agent1.skill.weather", schema);
        }

        [Fact]
        public void CliComponentBodyFolders_AreDerivedFromCliRules_AndExcludeNestedFileFolder()
        {
            // The D30 allowlist scan folders are derived from CliRules (single source of
            // truth), so the sync new-file scan cannot drift from the projection.
            var folders = LspProjection.CliComponentBodyFolders.OrderBy(f => f, StringComparer.Ordinal).ToArray();

            Assert.Equal(
                new[] { "behaviors", "capabilities/knowledge", "capabilities/tools" },
                folders);

            // Every scan folder is a real CLI projection folder (no invented paths).
            var ruleFolders = LspProjection.CliRules.Values
                .Select(r => r.Folder.TrimEnd('/'))
                .ToHashSet(StringComparer.Ordinal);
            Assert.All(folders, f => Assert.Contains(f, ruleFolders));

            // The nested file-attachment content folder is excluded from the scan roots
            // (it is handled by the dedicated knowledge-file path; the direct-child guard
            // keeps it out of the capabilities/knowledge/ scan).
            Assert.DoesNotContain("capabilities/knowledge/files", folders);
        }

        [Fact]
        public void CliFileAttachment_SubAgentKnowledgeFiles_RouteToCapabilities_UnderCliCopilot()
        {
            // TDD D38 review follow-up: a CLI sub-agent file attachment routes to
            // agents/<agent>/capabilities/knowledge/files/ (the sub-agent prefix + the CLI
            // knowledge-files folder), while classic stays at agents/<agent>/knowledge/files/.
            // This is the projection semantic the TS client child-agent helpers must mirror.
            const string schema = "agent1.file.MyDoc";
            const string subAgentFolder = "agents/SubAgent/";

            var cli = LspProjection.GetFilePath(
                typeof(FileAttachmentComponent), schema, BotName, subAgentFolder,
                pathWithoutExtension: null, AuthoringShape.CliCopilot);
            var classic = LspProjection.GetFilePath(
                typeof(FileAttachmentComponent), schema, BotName, subAgentFolder,
                pathWithoutExtension: null, AuthoringShape.Classic);

            Assert.NotNull(cli);
            Assert.NotNull(classic);
            Assert.StartsWith("agents/SubAgent/capabilities/knowledge/files/", cli);
            Assert.StartsWith("agents/SubAgent/knowledge/files/", classic);
            Assert.NotEqual(cli, classic);
        }
    }
}
