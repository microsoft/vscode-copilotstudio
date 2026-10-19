namespace Microsoft.PowerPlatformLS.UnitTests.Impl.Language.CopilotStudio
{
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Completion;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Completion;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Models;
    using System;
    using System.IO;
    using System.Linq;
    using Xunit;

    public class UnreadableDocumentResilienceTests
    {
        private const string DuplicateKeySettings =
            "displayName: N2 A8\nschemaName: cr834_n2a8\nconfiguration:\n  recognizer:\n    kind: CLICopilotRecognizer\ntemplate: cliagent-1.0.0\ntemplate: cliagent-1.0.0\nlanguage: 1033\n";

        private const string ValidSettings =
            "displayName: N2 A8\nschemaName: cr834_n2a8\nconfiguration:\n  recognizer:\n    kind: CLICopilotRecognizer\ntemplate: cliagent-1.0.0\nlanguage: 1033\n";

        private static (World World, McsWorkspace Workspace, string Root) CreateWorkspace(string settingsText, string? knowledgeRelativePath = null)
        {
            var root = Path.Combine(Path.GetTempPath(), "unreadable-" + Guid.NewGuid().ToString("N")).Replace('\\', '/');
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "settings.mcs.yml"), settingsText);

            if (knowledgeRelativePath != null)
            {
                var knowledgePath = Path.Combine(root, knowledgeRelativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(knowledgePath)!);
                File.WriteAllText(knowledgePath, "mcs.metadata:\n  componentName: File1.txt\nkind: SearchAndSummarizeContent\n");
            }

            var world = new World(root);
            var workspace = world.GetWorkspace();
            workspace.BuildCompilationModel();
            return (world, workspace, root);
        }

        [Fact]
        public void GetCurrentElement_OnUnreadableDocument_ReturnsNullInsteadOfThrowing()
        {
            var (world, _, root) = CreateWorkspace(DuplicateKeySettings);
            try
            {
                var document = world.GetDocument(new Uri(Path.Combine(root, "settings.mcs.yml")));
                Assert.NotNull(document);

                var context = world.GetRequestContext(document!, 0);

                Assert.Null(context.GetCurrentElement());
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void TryGetDocumentRoot_OnUnreadableDocument_ReportsFailureWithoutThrowing()
        {
            var (world, workspace, root) = CreateWorkspace(DuplicateKeySettings);
            try
            {
                var document = world.GetDocument(new Uri(Path.Combine(root, "settings.mcs.yml")));

                Assert.False(workspace.RequiredCompliationAnalyzer.TryGetDocumentRoot(document!, out var element));
                Assert.Null(element);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void UnreadableSettings_DoesNotReportAgentFileMissingOnAnUnrelatedFile()
        {
            var (world, workspace, root) = CreateWorkspace(DuplicateKeySettings, "capabilities/knowledge/files/File1.txt.mcs.yml");
            try
            {
                Assert.DoesNotContain("Agent file is missing.", AllDiagnosticMessages(world, workspace, root));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void UnreadableSettings_StillReportsTheDuplicateKeyOnTheOffendingFile()
        {
            var (world, workspace, root) = CreateWorkspace(DuplicateKeySettings);
            try
            {
                Assert.Contains(AllDiagnosticMessages(world, workspace, root), message => message.Contains("Duplicate key 'template'", StringComparison.Ordinal));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void ReadableSettings_ReportsNoDuplicateKeyOrAgentFileMissing()
        {
            var (world, workspace, root) = CreateWorkspace(ValidSettings, "capabilities/knowledge/files/File1.txt.mcs.yml");
            try
            {
                var messages = AllDiagnosticMessages(world, workspace, root);

                Assert.DoesNotContain("Agent file is missing.", messages);
                Assert.DoesNotContain(messages, message => message.Contains("Duplicate key", StringComparison.Ordinal));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void Completion_OnUnreadableDocument_ReturnsNoItemsInsteadOfThrowing()
        {
            var (world, _, root) = CreateWorkspace(DuplicateKeySettings);
            try
            {
                var document = world.GetDocument(new Uri(Path.Combine(root, "settings.mcs.yml")))!;
                var context = world.GetRequestContext(document, 0);
                var rule = world.GetRequiredServices<ICompletionRule<McsLspDocument>>().OfType<CopilotStudioCompletionRule>().Single();

                Assert.Empty(rule.ComputeCompletion(context, new CompletionContext()));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        private static string[] AllDiagnosticMessages(World world, McsWorkspace workspace, string root)
        {
            var document = world.GetDocument(new Uri(Path.Combine(root, "settings.mcs.yml")))!;
            var context = world.GetRequestContext(document, 0);
            return workspace.GetDiagnostics(context).SelectMany(parameters => parameters.Diagnostics).Select(diagnostic => diagnostic.Message).ToArray();
        }
    }
}
