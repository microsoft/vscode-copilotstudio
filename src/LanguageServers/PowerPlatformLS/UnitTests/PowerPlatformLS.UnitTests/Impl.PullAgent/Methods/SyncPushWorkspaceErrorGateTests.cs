namespace Microsoft.PowerPlatformLS.UnitTests.Impl.PullAgent.Methods
{
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.CopilotStudio.Sync;
    using Microsoft.PowerPlatformLS.Impl.PullAgent;
    using Microsoft.PowerPlatformLS.UnitTests.Impl.Language.CopilotStudio;
    using System;
    using System.IO;
    using System.Linq;
    using Xunit;

    public class SyncPushWorkspaceErrorGateTests
    {
        private static string CreateWorkspace()
        {
            var source = Path.GetFullPath(Path.Combine("TestData", "Workspace", "NestedSkillWorkspace"));
            var destination = Path.Combine(Path.GetTempPath(), "push-gate-" + Guid.NewGuid().ToString("N"));
            CopyDirectory(source, destination);
            return destination;
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (var file in Directory.EnumerateFiles(source))
            {
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
            }

            foreach (var directory in Directory.EnumerateDirectories(source))
            {
                CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
            }
        }

        private static Exception? GateFailureFor(string relativePath, string content)
        {
            var workspaceFolder = CreateWorkspace();
            try
            {
                if (content.Length > 0)
                {
                    File.WriteAllText(Path.Combine(workspaceFolder, relativePath.Replace('/', Path.DirectorySeparatorChar)), content);
                }

                var world = new World(workspaceFolder);
                var workspace = world.GetWorkspace();
                workspace.BuildCompilationModel();

                return Record.Exception(() => SyncHandler.ThrowIfWorkspaceUnreadable(workspace));
            }
            finally
            {
                Directory.Delete(workspaceFolder, recursive: true);
            }
        }

        [Fact]
        public void CleanWorkspace_IsNotBlocked()
        {
            var failure = GateFailureFor("behaviors/get-us-weather/skill.mcs.yml", string.Empty);

            Assert.True(failure == null, failure?.Message);
        }

        [Fact]
        public void ConflictedDocument_IsBlocked()
        {
            var failure = GateFailureFor("behaviors/get-us-weather/skill.mcs.yml", "kind: InlineAgentSkill\n<<<<<<< ours\ncontent: local\n=======\ncontent: remote\n>>>>>>> theirs\n");

            Assert.NotNull(failure);
            Assert.Contains(McsConflictMarkers.Message, failure!.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void DuplicateKeyDocument_IsBlocked()
        {
            var failure = GateFailureFor("behaviors/get-us-weather/skill.mcs.yml", "kind: InlineAgentSkill\ncontent: one\ncontent: two\n");

            Assert.NotNull(failure);
        }

        [Fact]
        public void TabIndentedDocument_IsBlocked()
        {
            var failure = GateFailureFor("behaviors/get-us-weather/skill.mcs.yml", "kind: InlineAgentSkill\nhistoryType:\n\tkind: ConversationHistory\n");

            Assert.NotNull(failure);
        }

        [Fact]
        public void UnterminatedQuoteDocument_IsBlocked()
        {
            var failure = GateFailureFor("behaviors/get-us-weather/skill.mcs.yml", "kind: InlineAgentSkill\ncontent: \"unterminated\n");

            Assert.NotNull(failure);
        }

        [Fact]
        public void BlockedWorkspace_NamesTheOffendingFile()
        {
            var failure = GateFailureFor("behaviors/get-us-weather/skill.mcs.yml", "kind: InlineAgentSkill\n<<<<<<< ours\ncontent: local\n=======\ncontent: remote\n>>>>>>> theirs\n");

            Assert.NotNull(failure);
            Assert.Contains("skill.mcs.yml", failure!.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData("settings.mcs.yml", "displayName: one\ndisplayName: two\n", WorkspaceDiagnosticKind.InvalidFile, 2)]
        [InlineData("behaviors/get-us-weather/skill.mcs.yml", "kind: InlineAgentSkill\ncontent: one\ncontent: two\n", WorkspaceDiagnosticKind.InvalidFile, 3)]
        [InlineData("settings.mcs.yml", "\n<<<<<<< ours\ndisplayName: one\n=======\ndisplayName: two\n>>>>>>> theirs\n", WorkspaceDiagnosticKind.MergeConflict, 2)]
        public void BlockedWorkspace_UsesSharedDiagnosticContract(string path, string content, WorkspaceDiagnosticKind kind, int line)
        {
            var failure = Assert.IsType<WorkspaceValidationException>(GateFailureFor(path, content));

            var diagnostic = Assert.Single(failure.Diagnostics);
            Assert.Equal(path, diagnostic.FilePath);
            Assert.Equal(kind, diagnostic.Kind);
            Assert.Equal(line, diagnostic.Line);
            Assert.Equal(1, diagnostic.Column);
        }
    }
}
