namespace Microsoft.PowerPlatformLS.UnitTests.Impl.Language.CopilotStudio
{
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.Extensions.FileProviders;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Utilities;
    using Moq;
    using System;
    using System.Linq;
    using Xunit;

    /// <summary>
    /// Regression coverage for CLI agent-root recognition (post-review Node S9). Before the fix,
    /// <see cref="AgentFilesAnalyzer.IsStrictAgentDirectory"/> keyed only on the classic
    /// <c>agent.mcs.yml</c> = <c>GptComponentMetadata</c>, so a CLI-layered workspace (which has no
    /// <c>agent.mcs.yml</c>, TDD D22) was never recognized as a strict agent root. The editor then
    /// fell back to the loose folder-guess, which can mis-root a CLI subfolder (e.g. <c>capabilities/</c>
    /// matches the classic <c>knowledge/</c> key via <c>capabilities/knowledge/</c>), degrading
    /// IntelliSense. The <c>agent.sync.yaml</c> layout marker (TDD D29) is dispositive for a
    /// CliCopilot workspace and is now a first-class strict signal.
    /// </summary>
    public class AgentFilesAnalyzerCliTests
    {
        [Fact]
        public void IsStrictAgentDirectory_CliWorkspaceWithSyncMarker_NoAgentMcsYml_ReturnsTrue()
        {
            // CLI agent root: agent.sync.yaml marker present, no agent.mcs.yml (D22/D29).
            var analyzer = new AgentFilesAnalyzer(FileProviderWith("agent.sync.yaml"));

            Assert.True(analyzer.IsStrictAgentDirectory(new DirectoryPath("c:/agent")));
        }

        [Fact]
        public void IsStrictAgentDirectory_NoAgentFileNoMarker_ReturnsFalse()
        {
            // Not an agent root: no agent.mcs.yml, no collection.mcs.yml, no agent.sync.yaml.
            var analyzer = new AgentFilesAnalyzer(FileProviderWith());

            Assert.False(analyzer.IsStrictAgentDirectory(new DirectoryPath("c:/agent")));
        }

        // File provider where only the given relative file names "exist" (Exists == true). The
        // strict recognizer only probes file existence (agent.mcs.yml / collection.mcs.yml /
        // agent.sync.yaml), so no file content is required for these cases.
        private static IClientWorkspaceFileProvider FileProviderWith(params string[] existingRelativeNames)
        {
            var mock = new Mock<IClientWorkspaceFileProvider>();
            mock
                .Setup(p => p.GetFileInfo(It.IsAny<FilePath>()))
                .Returns((FilePath p) =>
                {
                    var normalized = p.ToString().Replace('\\', '/');
                    var exists = existingRelativeNames.Any(name =>
                        normalized.EndsWith("/" + name, StringComparison.OrdinalIgnoreCase));

                    var info = new Mock<IFileInfo>();
                    info.SetupGet(f => f.Exists).Returns(exists);
                    return info.Object;
                });

            return mock.Object;
        }
    }
}
