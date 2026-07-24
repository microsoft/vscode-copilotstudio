namespace Microsoft.PowerPlatformLS.UnitTests.Contracts.Internal
{
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using Microsoft.PowerPlatformLS.UnitTests.TestUtilities;
    using Xunit;

    [Collection("LoggingTestsCollection")]
    public class LspRequestContextTests
    {
        public LspRequestContextTests()
        {
            LspRequestContext.CurrentRequestId = 0;
        }

        [Fact]
        public void SetAgentContext_Sets_All_Fields()
        {
            LspRequestContext.CurrentRequestId = 1;

            LspRequestContext.SetAgentContext("MyBot", "abc-123", "DevEnv", "env-456");

            Assert.Equal("MyBot", LspRequestContext.AgentName);
            Assert.Equal("abc-123", LspRequestContext.AgentId);
            Assert.Equal("DevEnv", LspRequestContext.EnvironmentName);
            Assert.Equal("env-456", LspRequestContext.EnvironmentId);
        }

        [Fact]
        public void CurrentRequestId_Resets_AgentContext()
        {
            LspRequestContext.CurrentRequestId = 1;
            LspRequestContext.SetAgentContext("Bot1", "id-1", "Env1", "eid-1");

            LspRequestContext.CurrentRequestId = 2;

            Assert.Null(LspRequestContext.AgentName);
            Assert.Null(LspRequestContext.AgentId);
        }

        [Fact]
        public void WithDuration_Sets_And_Clears_PendingDurationMs()
        {
            var logger = new TestLogger<LspRequestContextTests>(new TestLogger());

            using (LspRequestContext.WithDuration(42, logger))
            {
                Assert.Equal(42, LspRequestContext.PendingDurationMs);
            }

            Assert.Null(LspRequestContext.PendingDurationMs);
        }

        [Fact]
        public void SuppressOutputContext_Sets_And_Clears_Flag()
        {
            LspRequestContext.CurrentRequestId = 1;

            Assert.False(LspRequestContext.IsOutputContextSuppressed);

            using (LspRequestContext.SuppressOutputContext())
            {
                Assert.True(LspRequestContext.IsOutputContextSuppressed);
            }

            Assert.False(LspRequestContext.IsOutputContextSuppressed);
        }

        [Fact]
        public void AgentContext_Properties_Return_Null_Before_Set()
        {
            LspRequestContext.CurrentRequestId = 1;

            Assert.Null(LspRequestContext.AgentName);
            Assert.Null(LspRequestContext.AgentId);
            Assert.Null(LspRequestContext.EnvironmentName);
            Assert.Null(LspRequestContext.EnvironmentId);
        }

        [Fact]
        public void SetAgentContext_Allows_Null_Fields()
        {
            LspRequestContext.CurrentRequestId = 1;

            LspRequestContext.SetAgentContext(null, "id-1", null, "eid-1");

            Assert.Null(LspRequestContext.AgentName);
            Assert.Equal("id-1", LspRequestContext.AgentId);
            Assert.Null(LspRequestContext.EnvironmentName);
            Assert.Equal("eid-1", LspRequestContext.EnvironmentId);
        }
    }
}
