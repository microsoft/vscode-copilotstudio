namespace Microsoft.PowerPlatformLS.UnitTests.Impl.PullAgent.Methods
{
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.CopilotStudio.McsCore.Yaml;
    using Microsoft.CopilotStudio.Sync;
    using Microsoft.Agents.ObjectModel;
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.CopilotStudio.Sync.Dataverse;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Models;
    using Microsoft.PowerPlatformLS.Impl.PullAgent;
    using Microsoft.PowerPlatformLS.Impl.PullAgent.Auth;
    using Microsoft.PowerPlatformLS.UnitTests.Impl.Language.CopilotStudio;
    using Moq;
    using System;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
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
                    var filePath = Path.Combine(workspaceFolder, relativePath.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
                    File.WriteAllText(filePath, content);
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

        [Theory]
        [InlineData("settings.mcs.yml", "displayName: \"show <<<<<<< in the example\"\n")]
        [InlineData("behaviors/get-us-weather/skill.mcs.yml", "kind: InlineAgentSkill\ncontent: \"show >>>>>>> in the example\"\n")]
        [InlineData("behaviors/get-us-weather/skill.mcs.yml", "kind: InlineAgentSkill\ncontent: |\n  show <<<<<<< and >>>>>>> in the example\n")]
        [InlineData("behaviors/get-us-weather/skill.mcs.yml", "kind: InlineAgentSkill\ncontent: |\n  <<<<<<< opens a conflict\n  >>>>>>> closes one\n")]
        [InlineData("behaviors/get-us-weather/skill.mcs.yml", "kind: InlineAgentSkill\ncontent: |\n  <<<<<<< ours\n  local text\n  =======\n  remote text\n  >>>>>>> theirs\n")]
        public void LiteralMarkerText_IsNotBlocked(string path, string content)
        {
            Assert.Null(GateFailureFor(path, content));
        }

        [Theory]
        [InlineData("behaviors/get-us-weather/skill.mcs.yml", "kind: InlineAgentSkill\n<<<<<<< ours\ncontent: local text\n=======\ncontent: remote text\n>>>>>>> theirs\n")]
        [InlineData("settings.mcs.yml", "<<<<<<< ours\ndisplayName: local\n=======\ndisplayName: remote\n>>>>>>> theirs\n")]
        public void ColumnZeroConflictBlock_IsBlocked(string path, string content)
        {
            var failure = GateFailureFor(path, content);

            Assert.NotNull(failure);
            Assert.Contains(McsConflictMarkers.Message, failure!.Message, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("entities/Unsupported.mcs.yml", "kind: UnknownElement\ncontent: local text\n")]
        [InlineData("entities/Unsupported.mcs.yml", "kind: NotARealKind\n")]
        public void SemanticallyInvalidDocument_IsBlocked(string path, string content)
        {
            var failure = Assert.IsType<WorkspaceValidationException>(GateFailureFor(path, content));

            var diagnostic = Assert.Single(failure.Diagnostics);
            Assert.Equal(path, diagnostic.FilePath);
            Assert.Equal(WorkspaceDiagnosticKind.InvalidFile, diagnostic.Kind);
        }

        [Theory]
        [InlineData("settings.mcs.yml", "displayName: \"\\q\"\n")]
        [InlineData("topics/Broken.mcs.yml", "kind: AdaptiveDialog\nmodelDescription: \"\\q\"\n")]
        public void DocumentParsingFailure_IsBlocked(string path, string content)
        {
            var world = new World();
            var document = new ParsingFailureDocument(path, content);
            world.GetWorkspace().AddDocument(document);
            Assert.Equal(McsYamlError.Syntax, Assert.Throws<McsYamlFormatException>(
                () => McsYamlReader.ParseDocument(content)).Error);
            McsYamlValidator.ThrowIfMalformed(content);
            Assert.Null(document.FileModel);
            Assert.True(document.ParsingInfo.HasError);

            var failure = Assert.Throws<WorkspaceValidationException>(
                () => SyncHandler.ThrowIfWorkspaceUnreadable(world.GetWorkspace()));

            var diagnostic = Assert.Single(failure.Diagnostics);
            Assert.Equal(path, diagnostic.FilePath);
            Assert.Equal(document.ParsingInfo.Diagnostic!.Message, diagnostic.Message);
            Assert.Equal(2, diagnostic.Line);
            Assert.Equal(4, diagnostic.Column);
        }

        [Theory]
        [InlineData("topics/Accepted.mcs.yml", "kind: AdaptiveDialog\n activity: \"{Topic.answer.text}\"\n\ninputType: {}\noutputType: {}")]
        [InlineData("topics/Accepted.mcs.yml", "kind: AdaptiveDialog\nbeginDialog:\n  kind: OnRecognizedIntent\n")]
        [InlineData("other/Accepted.mcs.yml", "kind: AdaptiveDialog\n")]
        public void ObjectModelAcceptedDocument_IsNotBlocked(string path, string content)
        {
            var world = new World();
            var document = world.AddFile(path, content, elementCheck: false);
            Assert.NotNull(document.FileModel);

            SyncHandler.ThrowIfWorkspaceUnreadable(world.GetWorkspace());
        }

        [Fact]
        public void RepairingDocumentParsingFailure_UnblocksWithoutStaleFailure()
        {
            var world = new World();
            var document = new ParsingFailureDocument("topics/Broken.mcs.yml", "kind: AdaptiveDialog\nmodelDescription: \"\\q\"\n");
            world.GetWorkspace().AddDocument(document);
            Assert.Throws<WorkspaceValidationException>(() => SyncHandler.ThrowIfWorkspaceUnreadable(world.GetWorkspace()));

            document.UpdateText("kind: AdaptiveDialog\n");

            Assert.Empty(world.GetWorkspace().GetUnreadableDocuments());
            Assert.NotNull(document.FileModel);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Sync_DocumentParsingFailure_BlocksBeforeAnySyncCalls(bool push)
        {
            var world = new World();
            var document = new ParsingFailureDocument("topics/Broken.mcs.yml", "kind: AdaptiveDialog\nbeginDialog: [\n");
            world.GetWorkspace().AddDocument(document);
            var island = new Mock<IIslandControlPlaneService>(MockBehavior.Strict);
            var sync = new Mock<IWorkspaceSynchronizer>(MockBehavior.Strict);
            var dataverse = new Mock<ISyncDataverseClient>(MockBehavior.Strict);
            var operationProvider = new Mock<IOperationContextProvider>(MockBehavior.Strict);
            var logger = new Mock<ILspLogger>(MockBehavior.Strict);
            var httpAccessor = new LspDataverseHttpClientAccessor(Mock.Of<ISyncAuthProvider>());
            SyncHandler handler = push
                ? new SyncPushHandler(island.Object, sync.Object, new TestTokenManager(), dataverse.Object, httpAccessor, operationProvider.Object, logger.Object)
                : new SyncPullHandler(island.Object, sync.Object, new TestTokenManager(), dataverse.Object, httpAccessor, operationProvider.Object, world.GetRequiredService<IClientWorkspaceFileProvider>(), logger.Object);

            var request = new SyncAgentRequest
            {
                WorkspaceUri = new Uri("file:///c:/agent/"),
                EnvironmentInfo = new EnvironmentInfo
                {
                    DisplayName = "Test",
                    EnvironmentId = "test",
                    DataverseUrl = "https://test.crm.dynamics.com",
                    AgentManagementUrl = "https://test.agentmanagement.com",
                },
                SolutionVersions = new SolutionInfo(),
                AccountInfo = new AccountInfo(),
                DataverseAccessToken = string.Empty,
                CopilotStudioAccessToken = string.Empty,
            };

            var response = await handler.HandleRequestAsync(request, world.GetRequestContext(document, 0), CancellationToken.None);

            Assert.Equal(400, response.Code);
            Assert.Contains("topics/Broken.mcs.yml", response.Message, StringComparison.Ordinal);
            Assert.Contains(document.ParsingInfo.Diagnostic!.Message, response.Message, StringComparison.Ordinal);
            island.VerifyNoOtherCalls();
            sync.VerifyNoOtherCalls();
            dataverse.VerifyNoOtherCalls();
            operationProvider.VerifyNoOtherCalls();
            logger.VerifyNoOtherCalls();
        }

        private sealed class ParsingFailureDocument : McsLspDocument
        {
            private readonly string _invalidText;

            public ParsingFailureDocument(string path, string invalidText)
                : base(new FilePath("c:/agent/" + path), invalidText, new DirectoryPath("c:/agent"))
            {
                _invalidText = invalidText;
            }

            protected override BotElement? ComputeModel()
            {
                if (Text != _invalidText)
                {
                    return base.ComputeModel();
                }

                ParsingInfo.Diagnostic = new Diagnostic
                {
                    Message = "The document parser rejected this YAML syntax.",
                    Severity = DiagnosticSeverity.Error,
                    Range = new Microsoft.PowerPlatformLS.Contracts.Lsp.Models.Range
                    {
                        Start = new Position { Line = 1, Character = 3 },
                        End = new Position { Line = 1, Character = 4 },
                    },
                };
                return null;
            }
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
