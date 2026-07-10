namespace Microsoft.PowerPlatformLS.UnitTests.Impl.Language.CopilotStudio
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.Agents.Platform.Content;
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.CopilotStudio.Sync;
    using Microsoft.CopilotStudio.Sync.Dataverse;
    using Microsoft.Extensions.FileProviders;
    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models.Lsp;
    using Microsoft.PowerPlatformLS.UnitTests.Impl.PullAgent.Methods;
    using Microsoft.PowerPlatformLS.UnitTests.TestUtilities;
    using Moq;
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Xunit;

    public class WorkspaceUpsertDocumentTests
    {
        private const string LegacyHeaderContent =
            "# Name: My Topic\n" +
            "# A legacy description\n" +
            "kind: AdaptiveDialog\n" +
            "beginDialog:\n" +
            "  kind: OnRecognizedIntent\n" +
            "  intent:\n" +
            "    triggerQueries:\n" +
            "      - hello\n";

        private static readonly DirectoryPath WorkspaceFolderPath = new DirectoryPath(string.Empty);

        private static readonly AuthoringOperationContext FakeOperationContext =
            new AuthoringOperationContext(null, new CdsOrganizationInfo(), new BotReference(), null, false);

        [Fact]
        public void UpsertDocumentFromFile_DoesNotRewriteLegacyHeaderFileOnDisk()
        {
            var tempDir = Directory.CreateTempSubdirectory("mcs-upsert-test");
            try
            {
                var workspaceFolder = new DirectoryPath(tempDir.FullName.Replace('\\', '/'));
                var filePath = workspaceFolder.GetChildFilePath("Greeting.mcs.yml");
                File.WriteAllText(filePath.ToString(), LegacyHeaderContent);

                var workspace = new Workspace(workspaceFolder);
                var language = new FakeLanguage();

                var document = workspace.UpsertDocumentFromFile(filePath, CreateFileInfo(filePath.ToString(), LegacyHeaderContent), language, CultureInfo.InvariantCulture);

                Assert.Equal(LegacyHeaderContent, document.Text);

                var onDisk = File.ReadAllText(filePath.ToString());
                Assert.Equal(LegacyHeaderContent, onDisk);
                Assert.DoesNotContain("mcs.metadata", onDisk, StringComparison.Ordinal);
            }
            finally
            {
                tempDir.Delete(recursive: true);
            }
        }

        [Fact]
        public async Task Clone_WritesMcsMetadataIntoComponentFile()
        {
            var filesystem = new InMemoryFileWriter();
            var cancel = CancellationToken.None;

            var topicPath = new AgentFilePath("topics/thankYou.mcs.yml");
            await filesystem.WriteAsync(topicPath, LegacyHeaderContent, cancel);

            var component = new TestBotComponentFactory("cr123.topic.thankYou").CreateDialogComponent("thanks a lot");

            var changeset = new PvaComponentChangeSet(
                new List<BotComponentChange> { new BotComponentInsert(component) },
                new BotEntity().WithSchemaName(new BotEntitySchemaName("cr123")),
                "change-token-1");

            var islandControlPlaneServiceMock = new Mock<IIslandControlPlaneService>();
            islandControlPlaneServiceMock
                .Setup(x => x.GetComponentsAsync(FakeOperationContext, null, cancel))
                .Returns(Task.FromResult(changeset));

            var writer = new WorkspaceSynchronizer(
                new SyncMcsFileParser(LspProjectorService.Instance),
                (IFileAccessorFactory)filesystem,
                islandControlPlaneServiceMock.Object,
                Mock.Of<ISyncProgress>(),
                new LspComponentPathResolver());

            await writer.CloneChangesAsync(WorkspaceFolderPath, new ReferenceTracker(), FakeOperationContext, new MockDataverseClient(), new AgentSyncInfo { AgentId = Guid.NewGuid() }, cancel);

            var written = await filesystem.ReadStringAsync(topicPath, cancel);

            Assert.Contains("mcs.metadata:", written, StringComparison.Ordinal);
            Assert.DoesNotContain("# Name:", written, StringComparison.Ordinal);
        }

        private static IFileInfo CreateFileInfo(string filePath, string content)
        {
            var mock = new Mock<IFileInfo>();
            mock.SetupGet(f => f.Exists).Returns(true);
            mock.SetupGet(f => f.PhysicalPath).Returns(filePath);
            mock.Setup(f => f.CreateReadStream()).Returns(() => new MemoryStream(Encoding.UTF8.GetBytes(content)));
            return mock.Object;
        }

        private sealed class FakeLanguage : ILanguageAbstraction
        {
            public LanguageType LanguageType => LanguageType.CopilotStudio;

            public LspDocument CreateDocument(FilePath path, string text, CultureInfo culture, DirectoryPath workspacePath)
                => new FakeDocument(path, text, workspacePath);

            public bool IsValidAgentDirectory(DirectoryPath documentDirectory, out DirectoryPath validDirectory)
            {
                validDirectory = documentDirectory;
                return true;
            }
        }

        private sealed class FakeDocument : LspDocument
        {
            public FakeDocument(FilePath path, string text, DirectoryPath workspacePath)
                : base(path, text, Constants.LanguageIds.CopilotStudio, workspacePath)
            {
            }
        }
    }
}
