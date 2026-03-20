namespace Microsoft.PowerPlatformLS.UnitTests.Impl.Core.Lsp
{
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using Microsoft.PowerPlatformLS.Impl.Core.Lsp;
    using Microsoft.PowerPlatformLS.Impl.Core.Lsp.Uris;
    using Moq;
    using System.Text.Json;
    using Xunit;

    public class LspLanguageProviderIntegrationTests
    {
        [Fact]
        public void TryGetLanguageForDocument_FileLspUri_Success()
        {
            // Arrange
            var mockLanguage = new Mock<ILanguageAbstraction>();
            mockLanguage.Setup(x => x.LanguageType).Returns(LanguageType.CopilotStudio);
            
            var mockServices = new Mock<ILspServices>();
            var mockLogger = new Mock<ILspLogger>();
            mockServices.Setup(x => x.GetRequiredService<ILspLogger>()).Returns(mockLogger.Object);
            
            var languages = new[] { mockLanguage.Object };
            var provider = new LspLanguageProvider(languages, mockServices.Object);
            
            // Create a file URI and convert to LspUri using the factory
            var fileUriJson = JsonSerializer.SerializeToElement("file:///c:/repo/test.mcs.yaml");
            var typedUri = LspUriFactory.FromJsonElement(fileUriJson);
            
            // Act
            var result = provider.TryGetLanguageForDocument(typedUri, out var language);
            
            // Assert
            Assert.True(result);
            Assert.NotNull(language);
            Assert.Equal(LanguageType.CopilotStudio, language!.LanguageType);
        }

        [Fact]
    public void TryGetLanguageForDocument_UnsupportedLspUri_ReturnsFalse()
        {
            // Arrange
            var mockLanguage = new Mock<ILanguageAbstraction>();
            mockLanguage.Setup(x => x.LanguageType).Returns(LanguageType.CopilotStudio);
            
            var mockServices = new Mock<ILspServices>();
            var mockLogger = new Mock<ILspLogger>();
            mockServices.Setup(x => x.GetRequiredService<ILspLogger>()).Returns(mockLogger.Object);
            
            var languages = new[] { mockLanguage.Object };
            var provider = new LspLanguageProvider(languages, mockServices.Object);

            // Create an unsupported URI using the factory - note using a real Scheme currently unsupported
            // If your change is to support this scheme, update this test in the same commit or PR.
            var unsupportedUriJson = JsonSerializer.SerializeToElement("vscode-notebook://workspace/foo.ipynb");
            var typedUri = LspUriFactory.FromJsonElement(unsupportedUriJson);
            
            // Verify it's currently unsupported (without binding to specific implementation type)
            Assert.False(typedUri.IsSupported);
            
            // Act
            var result = provider.TryGetLanguageForDocument(typedUri, out var language);
            
            // Assert
            Assert.False(result);
            Assert.Null(language); // No language selected for unsupported scheme (current contract)
        }

        [Fact]
        public void TryGetLanguageForDocument_FileViaTextDocumentShape_Success()
        {
            // Arrange
            var mockLanguage = new Mock<ILanguageAbstraction>();
            mockLanguage.Setup(x => x.LanguageType).Returns(LanguageType.CopilotStudio);

            var mockServices = new Mock<ILspServices>();
            var mockLogger = new Mock<ILspLogger>();
            mockServices.Setup(x => x.GetRequiredService<ILspLogger>()).Returns(mockLogger.Object);

            var languages = new[] { mockLanguage.Object };
            var provider = new LspLanguageProvider(languages, mockServices.Object);

            // Simulate typical LSP JSON shape: { textDocument: { uri: "..." } }
            var shaped = JsonSerializer.SerializeToElement(new
            {
                textDocument = new { uri = "file:///c:/repo/shape-test.mcs.yaml" }
            });
            var typedUri = LspUriFactory.FromJsonElement(shaped);

            // Act
            var result = provider.TryGetLanguageForDocument(typedUri, out var language);

            // Assert
            Assert.True(result);
            Assert.NotNull(language);
            Assert.Equal(LanguageType.CopilotStudio, language!.LanguageType);
        }
    }
}
