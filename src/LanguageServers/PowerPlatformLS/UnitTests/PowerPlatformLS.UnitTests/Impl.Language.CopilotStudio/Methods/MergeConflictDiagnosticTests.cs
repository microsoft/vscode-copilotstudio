namespace Microsoft.PowerPlatformLS.UnitTests.Impl.Language.CopilotStudio.Methods
{
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.DependencyInjection;
    using Microsoft.PowerPlatformLS.UnitTests.TestUtilities;
    using System;
    using System.Linq;
    using System.Threading.Tasks;
    using Xunit;

    public class MergeConflictDiagnosticTests
    {
        private const string ConflictedSkillAnchor =
            "mcs.metadata:\n" +
            "  componentName: skill-1\n" +
            "  schemaName: cr834_n2a8.skill.skill-1_X-1\n" +
            "kind: InlineAgentSkill\n" +
            "content: |\n" +
            "  ---\n" +
            "  name: skill-1\n" +
            "  ---\n" +
            "  <<<<<<< (Current Change)\n" +
            "  Local 4 - When this skill is activated:\n" +
            "  =======\n" +
            "  Cloud 2 - When this skill is activated:\n" +
            "  >>>>>>> (Incoming Change)\n";

        [Fact]
        public async Task Diagnostic_OnConflictedSkillAnchor_Async()
        {
            var diagnostics = await GetDiagnosticsAsync("file:///c:/ws/behaviors/skill-1/skill.mcs.yml", ConflictedSkillAnchor);

            var error = Assert.Single(diagnostics);
            Assert.Equal(DiagnosticSeverity.Error, error.Severity);
            Assert.Equal("Unresolved merge conflict. Choose one side, remove the conflict markers, then sync again.", error.Message);
        }

        [Fact]
        public async Task Diagnostic_OnConflictedTopic_PointsAtTheFirstMarker_Async()
        {
            var diagnostics = await GetDiagnosticsAsync("file:///c:/ws/topics/Goodbye.mcs.yml", "kind: AdaptiveDialog\n<<<<<<< ours\ndisplayName: local\n=======\ndisplayName: remote\n>>>>>>> theirs\n");

            var error = Assert.Single(diagnostics);
            Assert.Equal(1, error.Range!.Value.Start.Line);
            Assert.Contains("Unresolved merge conflict", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Diagnostic_OnConflictedTopic_ReplacesTheParseError_Async()
        {
            var diagnostics = await GetDiagnosticsAsync("file:///c:/ws/topics/Goodbye.mcs.yml", "kind: AdaptiveDialog\n<<<<<<< ours\ndisplayName: local\n=======\ndisplayName: remote\n>>>>>>> theirs\n");

            Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Message.Contains("MissingRequiredProperty", StringComparison.Ordinal));
            Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Message.Contains("Failed to compute semantic model", StringComparison.Ordinal));
        }

        [Fact]
        public async Task NoDiagnostic_OnCleanTopic_Async()
        {
            var diagnostics = await GetDiagnosticsAsync("file:///c:/ws/topics/Goodbye.mcs.yml", "kind: AdaptiveDialog\n");

            Assert.Empty(diagnostics.Where(diagnostic => diagnostic.Message.Contains("Unresolved merge conflict", StringComparison.Ordinal)));
        }

        [Fact]
        public async Task NoDiagnostic_OnMarkerLikeTextInsideScalar_Async()
        {
            var diagnostics = await GetDiagnosticsAsync("file:///c:/ws/topics/Goodbye.mcs.yml", "kind: AdaptiveDialog\ndisplayName: \"use <<<< arrows\"\n");

            Assert.Empty(diagnostics.Where(diagnostic => diagnostic.Message.Contains("Unresolved merge conflict", StringComparison.Ordinal)));
        }

        [Fact]
        public async Task Diagnostic_OnDuplicateKeysInSettings_Async()
        {
            var diagnostics = await GetDiagnosticsAsync("file:///c:/ws/settings.mcs.yml", "displayName: N2 A8\ndisplayName: N2 A8 v2\nschemaName: cr834_n2a8\n");

            var error = Assert.Single(diagnostics);
            Assert.Equal(DiagnosticSeverity.Error, error.Severity);
            Assert.Contains("Duplicate key 'displayName'", error.Message, StringComparison.Ordinal);
            Assert.Equal(1, error.Range!.Value.Start.Line);
        }

        [Fact]
        public async Task Diagnostic_OnDuplicateKeysInTopic_Async()
        {
            var diagnostics = await GetDiagnosticsAsync("file:///c:/ws/topics/Goodbye.mcs.yml", "kind: AdaptiveDialog\ndisplayName: one\ndisplayName: two\n");

            Assert.Contains(diagnostics, diagnostic => diagnostic.Message.Contains("Duplicate key 'displayName'", StringComparison.Ordinal));
        }

        [Fact]
        public async Task Diagnostic_OnTabIndentation_Async()
        {
            var diagnostics = await GetDiagnosticsAsync("file:///c:/ws/settings.mcs.yml", "displayName: Agent\nconfiguration:\n\trecognizer: x\n");

            Assert.Contains(diagnostics, diagnostic => diagnostic.Message.Contains("Tabs are not allowed", StringComparison.Ordinal));
        }

        private static async Task<Diagnostic[]> GetDiagnosticsAsync(string uri, string text)
        {
            await using var context = new TestHost([new McsLspModule(), new TestFileModule()]);
            await context.InitializeLanguageServerAsync();
            var response = await context.OpenDocumentWithTextAsync(new Uri(uri), text);
            return response.Diagnostics;
        }
    }
}
