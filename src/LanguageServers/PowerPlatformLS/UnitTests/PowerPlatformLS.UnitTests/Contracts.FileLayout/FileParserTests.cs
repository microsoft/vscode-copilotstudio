namespace Microsoft.PowerPlatformLS.UnitTests.Contracts.FileLayout
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.PowerPlatformLS.Contracts.FileLayout;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Exceptions;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Models;
    using Xunit;

    public class FileParserTests
    {
        [Fact]
        public void Success_OnCompile_WithPathAndModel()
        {
            var parser = new McsFileParser();
            var result = parser.CompileFileModel(
                "TestSchema",
                new AdaptiveDialog());
            Assert.Null(result.error);
            Assert.NotNull(result.component);
        }
    }
}
