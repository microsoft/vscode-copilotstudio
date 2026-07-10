namespace Microsoft.PowerPlatformLS.Tools.LspJournalCli.UnitTests
{
    using System.Text.Json;
    using Microsoft.PowerPlatformLS.Tools.LspJournalCli.Transport;
    using Xunit;

    public sealed class SerializationOptionsTests
    {
        // Journal baselines are committed as LF (.gitattributes: *.json eol=lf). The tool
        // must write them with LF newlines directly so that regenerating a baseline on
        // Windows does not introduce CRLF churn that git then has to renormalize. Without
        // an explicit NewLine, System.Text.Json's indented writer uses Environment.NewLine
        // (CRLF on Windows), which is exactly the line-ending noise issue #313 is about.
        [Fact]
        public void Indented_WritesLfNewlines_NotCrlf()
        {
            var json = JsonSerializer.Serialize(
                new { first = 1, second = "two", nested = new { third = true } },
                SerializationOptions.Indented);

            Assert.Contains("\n", json);
            Assert.DoesNotContain("\r\n", json);
            Assert.DoesNotContain("\r", json);
        }
    }
}
