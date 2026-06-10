namespace Microsoft.PowerPlatformLS.UnitTests.Impl.PullAgent
{
    using Microsoft.CopilotStudio.McsCore;
    using System;
    using Xunit;

    /// <summary>
    /// Node S5 (TDD D35): the fail-closed support gate (<see cref="AuthoringSupportGate"/>) is
    /// ENFORCED, not merely tested. Clone/pull allow a Provisional shape (bootstrap); push and
    /// reattach require Supported. <c>SyncPushHandler</c> calls <c>EnsureAllowed(.., Push)</c>
    /// and <c>ReattachAgentHandler</c> gates on <c>Allows(Reattach)</c> + <c>DescribeBlocked</c>;
    /// these tests lock the gate's decision matrix.
    /// </summary>
    public class AuthoringSupportGateTests
    {
        private static AgentClassification SupportedCli =>
            new(AuthoringShape.CliCopilot, WorkspaceLayout.CliLayered, SupportLevel.Supported, null, "test-cli");

        private static AgentClassification SupportedClassic =>
            new(AuthoringShape.Classic, WorkspaceLayout.ClassicMcs, SupportLevel.Supported, null, "test-classic");

        private static AgentClassification Provisional =>
            new(AuthoringShape.Unknown, WorkspaceLayout.Unknown, SupportLevel.Provisional, "unrecognized-shape", "test-provisional");

        private static AgentClassification Unsupported => AgentClassification.None;

        [Theory]
        [InlineData(SyncOperation.Inspect)]
        [InlineData(SyncOperation.Clone)]
        [InlineData(SyncOperation.Pull)]
        [InlineData(SyncOperation.Push)]
        [InlineData(SyncOperation.Reattach)]
        public void Supported_AllowsEveryOperation(SyncOperation operation)
        {
            // No throw for either recognized shape.
            AuthoringSupportGate.EnsureAllowed(SupportedCli, operation);
            AuthoringSupportGate.EnsureAllowed(SupportedClassic, operation);
            Assert.True(SupportedCli.Allows(operation));
            Assert.True(SupportedClassic.Allows(operation));
        }

        [Theory]
        [InlineData(SyncOperation.Inspect)]
        [InlineData(SyncOperation.Clone)]
        [InlineData(SyncOperation.Pull)]
        public void Provisional_AllowsCloneAndPull(SyncOperation operation)
        {
            AuthoringSupportGate.EnsureAllowed(Provisional, operation);
            Assert.True(Provisional.Allows(operation));
        }

        [Theory]
        [InlineData(SyncOperation.Push)]
        [InlineData(SyncOperation.Reattach)]
        public void Provisional_BlocksPushAndReattach(SyncOperation operation)
        {
            Assert.False(Provisional.Allows(operation));
            var ex = Assert.Throws<InvalidOperationException>(
                () => AuthoringSupportGate.EnsureAllowed(Provisional, operation));
            Assert.Contains(operation.ToString(), ex.Message);

            var message = AuthoringSupportGate.DescribeBlocked(Provisional, operation);
            Assert.Contains(operation.ToString(), message);
            // The preserved raw shape value of an unrecognized agent is surfaced.
            Assert.Contains("unrecognized-shape", message);
        }

        [Theory]
        [InlineData(SyncOperation.Clone)]
        [InlineData(SyncOperation.Pull)]
        [InlineData(SyncOperation.Push)]
        [InlineData(SyncOperation.Reattach)]
        public void Unsupported_BlocksEverythingExceptInspect(SyncOperation operation)
        {
            Assert.False(Unsupported.Allows(operation));
            Assert.Throws<InvalidOperationException>(
                () => AuthoringSupportGate.EnsureAllowed(Unsupported, operation));
        }

        [Fact]
        public void Unsupported_AllowsInspect()
        {
            AuthoringSupportGate.EnsureAllowed(Unsupported, SyncOperation.Inspect);
            Assert.True(Unsupported.Allows(SyncOperation.Inspect));
        }
    }
}
