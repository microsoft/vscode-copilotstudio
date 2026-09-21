namespace Microsoft.PowerPlatformLS.UnitTests.Impl.Language.Yml
{
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Impl.Language.Yaml.DependencyInjection;
    using Microsoft.PowerPlatformLS.UnitTests.TestUtilities;
    using System;
    using System.Linq;
    using System.Threading.Tasks;
    using Xunit;

    public class YamlLanguageTests
    {
        [Fact]
        public async Task Success_OnValidYaml_Async()
        {
            const string YamlText = @"
            name: test
            type: test
            properties:
              key: value
            ";
            var diagnostics = await GetDiagnosticsForYamlTextAsync(YamlText);

            // assert
            Assert.Empty(diagnostics);
        }

        [Fact]
        public async Task Diagnostic_OnSemanticError_Async()
        {
            const string YamlText = "  name: test\ntype: test";
            var diagnostics = await GetDiagnosticsForYamlTextAsync(YamlText);

            var error = diagnostics.Single();
            Assert.Equal(DiagnosticSeverity.Error, error.Severity);
            Assert.DoesNotContain("Unhandled exception", error.Message, StringComparison.Ordinal);
            Assert.NotNull(error.Range);
        }

        [Fact]
        public async Task NoDiagnostic_OnEmptyText_Async()
        {
            Assert.Empty(await GetDiagnosticsForYamlTextAsync(string.Empty));
        }

        [Fact]
        public async Task Diagnostic_OnDuplicateIds_Async()
        {
            var diagnostics = await GetDiagnosticsForYamlTextAsync("items:\n- id: repeated\n- id: repeated\n");

            var error = diagnostics.Single();
            Assert.Equal(DiagnosticSeverity.Error, error.Severity);
            Assert.Contains("Duplicate id 'repeated'", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task NoDiagnostic_OnUniqueIds_Async()
        {
            Assert.Empty(await GetDiagnosticsForYamlTextAsync("items:\n- id: first\n- id: second\n"));
        }

        [Fact]
        public async Task Diagnostic_OnDuplicateKeys_Async()
        {
            var diagnostics = await GetDiagnosticsForYamlTextAsync("name: one\nname: two\n");

            var error = diagnostics.Single();
            Assert.Equal(DiagnosticSeverity.Error, error.Severity);
            Assert.Contains("Duplicate key name", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Diagnostic_OnDuplicateNestedKeys_Async()
        {
            var diagnostics = await GetDiagnosticsForYamlTextAsync("outer:\n  inner: one\n  inner: two\n");

            Assert.Contains(diagnostics, diagnostic => diagnostic.Message.Contains("Duplicate key inner", StringComparison.Ordinal));
        }

        [Fact]
        public async Task NoDiagnostic_OnSameKeyInSiblingMappings_Async()
        {
            Assert.Empty(await GetDiagnosticsForYamlTextAsync("items:\n- name: one\n- name: two\n"));
        }

        [Fact]
        public async Task Diagnostic_OnNonStringId_Async()
        {
            var diagnostics = await GetDiagnosticsForYamlTextAsync("id:\n  nested: value\n");

            Assert.Contains(diagnostics, diagnostic => diagnostic.Message.Contains("should be a string", StringComparison.Ordinal));
        }

        [Fact]
        public async Task NoDiagnostic_OnLayoutMarker_WithEmptyText_Async()
        {
            var diagnostics = await GetDiagnosticsForYamlTextAsync(string.Empty, new Uri("file:///c:/agent/agent.sync.yaml"));

            Assert.Empty(diagnostics);
        }

        [Fact]
        public async Task NoDiagnostic_OnLayoutMarker_WithSemanticErrorText_Async()
        {
            const string YamlText = "  name: test\ntype: test";
            var diagnostics = await GetDiagnosticsForYamlTextAsync(YamlText, new Uri("file:///c:/agent/agent.sync.yaml"));

            Assert.Empty(diagnostics);
        }

        private async Task<Diagnostic[]> GetDiagnosticsForYamlTextAsync(string text, Uri? documentUri = null)
        {
            await using var context = new TestHost([new YamlLspModule()]);
            await context.InitializeLanguageServerAsync();
            var response = await context.OpenDocumentWithTextAsync(documentUri ?? new Uri("file:///c:/new_file.yml"), text);
            return response.Diagnostics;
        }
    }
}
