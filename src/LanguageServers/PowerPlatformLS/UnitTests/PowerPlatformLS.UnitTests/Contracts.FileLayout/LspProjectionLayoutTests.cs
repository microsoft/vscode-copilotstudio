namespace Microsoft.PowerPlatformLS.UnitTests.Contracts.FileLayout
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.CopilotStudio.McsCore;
    using Xunit;

    [Trait("Category", "Projection")]
    public class LspProjectionLayoutTests
    {
        [Theory]
        [InlineData("behaviors/get-us-weather_peu/skillmd_dWNAJ", true)]
        [InlineData("behaviors/get-us-weather_peu/scriptsgetusweatherps1_9GRrm", true)]
        [InlineData("behaviors/x/y", true)]
        [InlineData("behaviors/x/y/z", true)]
        [InlineData("behaviors/get-us-weather_peu", false)]
        [InlineData("behaviors", false)]
        [InlineData("capabilities/knowledge/files/MyFile", false)]
        [InlineData("topics/Foo", false)]
        public void TryGetPackagedSkillPayloadTypes_MatchesOnlyBehaviorsSkillPayloadSidecar(string path, bool expectedMatch)
        {
            var matched = LspProjectionLayout.TryGetPackagedSkillPayloadTypes(new AgentFilePath(path), out var types);

            Assert.Equal(expectedMatch, matched);
            if (expectedMatch)
            {
                Assert.Equal(typeof(FileAttachmentComponent), Assert.Single(types));
            }
            else
            {
                Assert.Empty(types);
            }
        }

        [Fact]
        public void TryGetPackagedSkillPayloadTypes_SkillAnchor_MapsToInlineAgentSkill()
        {
            var matched = LspProjectionLayout.TryGetPackagedSkillPayloadTypes(new AgentFilePath("behaviors/get-us-weather/skill"), out var types);

            Assert.True(matched);
            Assert.Equal(typeof(InlineAgentSkill), Assert.Single(types));
        }
    }
}
