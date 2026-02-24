namespace Microsoft.PowerPlatformLS.UnitTests.Contracts.FileLayout
{
    using Microsoft.PowerPlatformLS.Contracts.FileLayout;
    using System;
    using Xunit;

    public class AgentFilePathTests
    {
        private const string MustBeCanonical = "AgentFilePath must be fully resolved. It must be a canonical relative path. It cannot refer to current or parent directory. (Parameter 'path')";

        [Theory]
        [InlineData("/absolute/path/to/file.mcs", "AgentFilePath must be relative to an agent directory. (Parameter 'path')")]
        [InlineData("subdir/../file.mcs", MustBeCanonical)]
        [InlineData("../file.mcs", MustBeCanonical)]
        [InlineData("./file.mcs", MustBeCanonical)]
        [InlineData("subdir/./file.mcs", MustBeCanonical)]
        [InlineData("subdir/.", MustBeCanonical)]
        [InlineData("subdir/..", MustBeCanonical)]
        public void ValidateConstraints(string pathValue, string expectedArgumentError)
        {
            var exc = Assert.Throws<ArgumentException>(() => new AgentFilePath(pathValue));
            Assert.Equal(expectedArgumentError, exc.Message);
        }

        [Fact]
        public void Success_OnValidPath()
        {
            var validPath = new AgentFilePath("subdir/file.mcs");
            Assert.Equal("file", validPath.FileNameWithoutExtension);
        }
    }
}
