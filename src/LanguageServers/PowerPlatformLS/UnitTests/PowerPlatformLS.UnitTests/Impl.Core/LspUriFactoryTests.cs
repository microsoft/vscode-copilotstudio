namespace Microsoft.PowerPlatformLS.UnitTests.Impl.Core.Lsp.Uris
{
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Impl.Core.Lsp.Uris;
    using Moq;
    using System;
    using System.Linq;
    using System.Text.Json;
    using Xunit;

    public class LspUriFactoryTests
    {
        private static readonly Mock<ILspLogger> MockLogger = new Mock<ILspLogger>();

        #region Factory Tests (Phase 1) - Pure ErrorObject Pattern

        [Theory]
        [InlineData("file:///c:/repo/x.mcs")]
        [InlineData("file:///home/dev/x.mcs")]
        public void Factory_File_Supported(string uriString)
        {
            var jsonElement = JsonSerializer.SerializeToElement(uriString);
            var result = LspUriFactory.FromJsonElement(jsonElement);

            Assert.IsType<FileLspUri>(result);
            Assert.True(result.IsSupported);
            Assert.Equal(uriString, result.Raw);
        }

        [Fact]
        public void Factory_Untitled_Unsupported()
        {
            var jsonElement = JsonSerializer.SerializeToElement("untitled:/c%3A/repo/x.mcs");
            var result = LspUriFactory.FromJsonElement(jsonElement);

            Assert.IsType<UnsupportedLspUri>(result);
            Assert.False(result.IsSupported);
            var unsupported = (UnsupportedLspUri)result;
            Assert.Equal(UnsupportedReasonCodes.UnsupportedScheme, unsupported.ReasonCode);
            Assert.Equal("untitled", unsupported.Scheme);
        }

        [Theory]
        [InlineData("git:/c:/repo/x.mcs.yml?ref=main", "git")]
        [InlineData("ssh://host/path/x.mcs.yml", "ssh")]
        [InlineData("merge-conflict.conflict-diff:/c:/repo/x.mcs.yml", "merge-conflict.conflict-diff")]
        [InlineData("remote://host/c:/repo/x.mcs.yml", "remote")]
        [InlineData("vscode-notebook://workspace/foo.ipynb", "vscode-notebook")]
        [InlineData("untitled:/c%3A/repo/x.mcs.yml", "untitled")]
        public void Factory_UnsupportedScheme_ReturnsUnsupportedLspUri(string uriString, string expectedScheme)
        {
            // NOTE: This test intentionally captures CURRENT behavior that these schemes (including 'untitled:')
            // are treated as unsupported by the URI factory. If/when support for any listed scheme is added,
            // update this test in the same commit to ensure the change is deliberate rather than accidental.
            var jsonElement = JsonSerializer.SerializeToElement(uriString);
            var result = LspUriFactory.FromJsonElement(jsonElement);

            Assert.IsType<UnsupportedLspUri>(result);
            Assert.False(result.IsSupported);
            var unsupported = (UnsupportedLspUri)result;
            Assert.Equal(UnsupportedReasonCodes.UnsupportedScheme, unsupported.ReasonCode);
            Assert.Equal(expectedScheme, unsupported.Scheme);
        }

        [Fact]
        public void Factory_ParseError()
        {
            // Test malformed JSON - number where string expected
            var jsonElement = JsonSerializer.SerializeToElement(42);
            var result = LspUriFactory.FromJsonElement(jsonElement);

            Assert.IsType<UnsupportedLspUri>(result);
            Assert.False(result.IsSupported);
            var unsupported = (UnsupportedLspUri)result;
            Assert.Equal(UnsupportedReasonCodes.ParseError, unsupported.ReasonCode);
        }

        [Fact]
        public void Factory_NotAbsolute()
        {
            // Test crafted relative URI that survives JSON parsing
            var jsonElement = JsonSerializer.SerializeToElement("x.mcs");
            var result = LspUriFactory.FromJsonElement(jsonElement);

            Assert.IsType<UnsupportedLspUri>(result);
            Assert.False(result.IsSupported);
            var unsupported = (UnsupportedLspUri)result;
            Assert.Equal(UnsupportedReasonCodes.NotAbsolute, unsupported.ReasonCode);
        }


        #endregion

        #region FromUri Factory Tests

        [Theory]
        [InlineData("ssh://host/path/file.mcs", "ssh")]
        [InlineData("git://repo/path/file.mcs", "git")]
        [InlineData("vscode-notebook://workspace/notebook.ipynb", "vscode-notebook")]
        [InlineData("vscode-notebook-cell://workspace/cell.py", "vscode-notebook-cell")]
        [InlineData("merge-conflict.conflict-diff://path/file.cs", "merge-conflict.conflict-diff")]
        [InlineData("untitled:/c%3A/repo/untitled.mcs", "untitled")]
        public void FromUri_UnsupportedSchemes_ReturnsUnsupportedLspUri(string uriString, string expectedScheme)
        {
            var uri = new Uri(uriString);
            var result = LspUriFactory.FromUri(uri);

            Assert.IsType<UnsupportedLspUri>(result);
            Assert.False(result.IsSupported);
            var unsupported = (UnsupportedLspUri)result;
            Assert.Equal(UnsupportedReasonCodes.UnsupportedScheme, unsupported.ReasonCode);
            Assert.Equal(expectedScheme, unsupported.Scheme);
            Assert.Equal(uriString, result.Raw);
        }

        [Fact]
        public void FromUri_ConsistentWithFromJsonElement()
        {
            var uriString = "file:///c:/repo/test.mcs";
            var uri = new Uri(uriString);
            var jsonElement = JsonSerializer.SerializeToElement(uriString);
            
            var fromUri = LspUriFactory.FromUri(uri);
            var fromJson = LspUriFactory.FromJsonElement(jsonElement);

            Assert.Equal(fromUri.GetType(), fromJson.GetType());
            Assert.Equal(fromUri.IsSupported, fromJson.IsSupported);
            Assert.Equal(fromUri.Raw, fromJson.Raw);
        }

        [Theory]
        [InlineData("ssh://host/file.cs")]
        [InlineData("git://repo/file.cs")]
        [InlineData("untitled:/file.cs")]
        public void FromUri_UnsupportedConsistentWithFromJsonElement(string uriString)
        {
            var uri = new Uri(uriString);
            var jsonElement = JsonSerializer.SerializeToElement(uriString);
            
            var fromUri = LspUriFactory.FromUri(uri);
            var fromJson = LspUriFactory.FromJsonElement(jsonElement);

            Assert.Equal(fromUri.GetType(), fromJson.GetType());
            Assert.Equal(fromUri.IsSupported, fromJson.IsSupported);
            Assert.Equal(fromUri.Raw, fromJson.Raw);
            
            // Both should be UnsupportedLspUri with same properties
            var unsupportedFromUri = Assert.IsType<UnsupportedLspUri>(fromUri);
            var unsupportedFromJson = Assert.IsType<UnsupportedLspUri>(fromJson);
            Assert.Equal(unsupportedFromUri.ReasonCode, unsupportedFromJson.ReasonCode);
            Assert.Equal(unsupportedFromUri.Scheme, unsupportedFromJson.Scheme);
        }

        #endregion

        #region Negative Robustness Tests

        [Fact]
        public void Factory_NullElement()
        {
            // Test with empty/null JSON object
            var emptyJson = JsonSerializer.SerializeToElement(new { });
            var result = LspUriFactory.FromJsonElement(emptyJson);

            Assert.IsType<UnsupportedLspUri>(result);
            Assert.False(result.IsSupported);
            var unsupported = (UnsupportedLspUri)result;
            Assert.Equal(UnsupportedReasonCodes.ParseError, unsupported.ReasonCode);
        }

        [Fact]
        public void Factory_ArbitraryUnknownScheme_IsUnsupported()
        {
            const string Scheme = "zzz-never-supported-test-scheme"; // PascalCase to satisfy naming rules for constants
            var uriString = $"{Scheme}://host/path/file.mcs";
            var jsonElement = JsonSerializer.SerializeToElement(uriString);
            var result = LspUriFactory.FromJsonElement(jsonElement);

            var unsupported = Assert.IsType<UnsupportedLspUri>(result);
            Assert.False(unsupported.IsSupported);
            Assert.Equal(UnsupportedReasonCodes.UnsupportedScheme, unsupported.ReasonCode);
            Assert.Equal(Scheme, unsupported.Scheme);
        }

        #endregion

        #region JSON Structure Tests (LSP Parameter Shapes)

        [Fact]
        public void Factory_TextDocument_Uri_Pattern()
        {
            var json = JsonSerializer.SerializeToElement(new
            {
                textDocument = new { uri = "file:///c:/repo/test.mcs" }
            });
            
            var result = LspUriFactory.FromJsonElement(json);

            Assert.IsType<FileLspUri>(result);
            Assert.True(result.IsSupported);
            Assert.Equal("file:///c:/repo/test.mcs", result.Raw);
        }

        [Fact]
        public void Factory_DirectUri_Pattern()
        {
            var json = JsonSerializer.SerializeToElement(new
            {
                uri = "file:///c:/repo/test.mcs"
            });
            
            var result = LspUriFactory.FromJsonElement(json);

            Assert.IsType<FileLspUri>(result);
            Assert.True(result.IsSupported);
            Assert.Equal("file:///c:/repo/test.mcs", result.Raw);
        }

        [Fact]
        public void Factory_ResolveData_Pattern()
        {
            var json = JsonSerializer.SerializeToElement(new
            {
                data = new
                {
                    TextDocument = new { uri = "file:///c:/repo/test.mcs" }
                }
            });
            
            var result = LspUriFactory.FromJsonElement(json);

            Assert.IsType<FileLspUri>(result);
            Assert.True(result.IsSupported);
            Assert.Equal("file:///c:/repo/test.mcs", result.Raw);
        }

        #endregion
    }
}
