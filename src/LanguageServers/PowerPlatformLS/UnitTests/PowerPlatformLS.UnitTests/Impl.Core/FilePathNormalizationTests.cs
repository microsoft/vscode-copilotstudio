namespace Microsoft.PowerPlatformLS.UnitTests.Impl.Core.Lsp.Uris
{
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Impl.Core.Lsp.Uris;
    using System;
    using System.IO;
    using Xunit;

    public class FilePathNormalizationTests
    {
        // This test replaces the prior cross-platform mutation test which overrode a global static
        // delegate (PlatformService.IsUnixPlatform) and caused race conditions under parallel runs.
        // We now simply assert deterministic, environment-agnostic properties of normalization
        // using the real platform indicator (no mutation) and encoded characters.
        [Theory]
        [InlineData("file:///c:/repo/a%20b.mcs.yml")]
        [InlineData("file:///c:/repo/a%23b.mcs.yml")]
        [InlineData("file:///c:/Repo/Dir/File.mcs.yml")]
        public void ToFilePath_Normalization_IsRootedAndDecodes(string uriString)
        {
            var uri = new Uri(uriString);
            var filePath = uri.ToFilePath().ToString();

            Assert.True(Path.IsPathRooted(filePath));

            // Ensure percent-encoded characters are decoded in the resulting path
            var unescaped = uri.GetComponents(UriComponents.Path, UriFormat.Unescaped);
            Assert.EndsWith(unescaped, filePath, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void AsFilePathNormalized_Idempotent()
        {
            var uri = new Uri("file:///c:/repo/file%20with%20spaces.mcs.yml");
            var fileLspUri = new FileLspUri(uri, uri.Scheme);
            var first = fileLspUri.AsFilePathNormalized().ToString();
            var second = fileLspUri.AsFilePathNormalized().ToString();
            Assert.Equal(first, second, ignoreCase: true);
        }
    }
}
