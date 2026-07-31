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
        [InlineData("behaviors/get-us-weather_peu", false)]
        [InlineData("behaviors", false)]
        [InlineData("behaviors/x/y/z", false)]
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
    }
}
