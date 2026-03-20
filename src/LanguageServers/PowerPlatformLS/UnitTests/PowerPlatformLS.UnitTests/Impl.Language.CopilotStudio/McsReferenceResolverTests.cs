namespace Microsoft.PowerPlatformLS.UnitTests.Impl.Language.CopilotStudio
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Exceptions;
    using System.IO;
    using Xunit;

    public class McsReferenceResolverTests
    {
        private readonly IReferenceResolver _resolver;
        private readonly DirectoryPath _workspacePath;

        public McsReferenceResolverTests()
        {
            var dir = Path.GetFullPath(Path.Combine("TestData", "WorkspaceWithCC"));
            World world = new World(dir);

            var workspace = world.GetWorkspace(Path.Combine(dir, "Agent 111"));

            _resolver = world.GetRequiredService<IReferenceResolver>();
            _workspacePath = workspace.FolderPath;
        }

        [Fact]
        public void SuccessulResolve()
        {
            // Success case. 
            var ccRef = new ReferenceItemSourceFile(schemaName: string.Empty, directory: "../MyCC333");

            var cc = _resolver.ResolveComponentCollectionOrThrow(_workspacePath, ccRef);

            Assert.Equal("bot_componentcollection_my_cc_333", cc.GetRootSchemaName());
        }

        [Theory]
        [InlineData("../MissingReference")]  // target dir does not exist.
        [InlineData("")]  // empty
        [InlineData(null)]  // empty
        [InlineData("../Agent 111")]  // exists, but not a CC
        public void FailToResolve(string dir)
        {
            // Success case. 
            var ccRef = new ReferenceItemSourceFile(
                schemaName: string.Empty,
                directory: dir);

            Assert.Throws<BadReferenceException>(() => _resolver.ResolveComponentCollectionOrThrow(_workspacePath, ccRef));
        }
    }
}
