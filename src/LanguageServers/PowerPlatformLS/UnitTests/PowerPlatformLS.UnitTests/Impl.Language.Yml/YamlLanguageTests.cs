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

            // assert
            var error = diagnostics.Single();
            Assert.Equal("Did not find expected <document end>.", error.Message);
        }

        [Fact]
        public async Task Diagnostic_OnEmptyText_Async()
        {
            var diagnostics = await GetDiagnosticsForYamlTextAsync(string.Empty);

            // assert
            var error = diagnostics.Single();
            Assert.StartsWith("Failed to compute semantic model. Unhandled exception: System.InvalidOperationException: Sequence contains no elements", error.Message);
        }

        private async Task<Diagnostic[]> GetDiagnosticsForYamlTextAsync(string text)
        {
            await using var context = new TestHost([new YamlLspModule()]);
            await context.InitializeLanguageServerAsync();
            var response = await context.OpenDocumentWithTextAsync(new Uri("file:///c:/new_file.yml"), text);
            return response.Diagnostics;
        }
    }
}
