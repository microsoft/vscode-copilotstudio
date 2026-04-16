namespace Microsoft.PowerPlatformLS.UnitTests.Contracts.FileLayout
{
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using Xunit;

    public class DirectoryPathTests
    {
        // Relative paths are used when one workspace refers to another.
        [Fact]
        public void RelativeDirs()
        {
            var f1 = new DirectoryPath(@"c:/stuff/agent");
            var f2 = new DirectoryPath(@"c:/stuff/cc");

            RelativeDirectoryPath f3 = f2.GetRelativeFrom(f1);

            Assert.Equal("../cc/", f3.ToString());

            var f1b = f1.ResolveRelativeRef(f3);
            Assert.Equal(f2, f1b);
        }
    }
}
