namespace Microsoft.PowerPlatformLS.UnitTests.Impl.Language.CopilotStudio
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Utilities;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Models;
    using Microsoft.PowerPlatformLS.UnitTests.TestUtilities;
    using System;
    using System.Linq;
    using Xunit;

    public class ObjectModelExtensionsTests
    {
        private const string TestData = "kind: Unknown";

        [Theory]
        [InlineData(typeof(UnknownBotElementIssue.Builder), DiagnosticSeverity.Information)]
        [InlineData(typeof(PropertyWarning.Builder), DiagnosticSeverity.Warning)]
        [InlineData(typeof(IncorrectTypeError.Builder), DiagnosticSeverity.Error)]
        [InlineData(typeof(PropertyError.Builder), DiagnosticSeverity.Error)]
        [InlineData(typeof(InvalidReferenceError.Builder), DiagnosticSeverity.Error)]
        [InlineData(typeof(ExpressionError.Builder), DiagnosticSeverity.Error)]
        public void Default_OnDiagnosticConversion(Type builderType, DiagnosticSeverity expectedSeverity)
        {
            var botDiag = ((BotElementDiagnostic.Builder)Activator.CreateInstance(builderType)!).Build();
            var lspDiag = botDiag.ToLspDiagnostics(new UnknownBotElement.Builder().Build(), new MarkResolver(TestData)).Single();
            Assert.Equal(expectedSeverity, lspDiag.Severity);
            Assert.Equal(PowerPlatformLS.Contracts.Lsp.Models.Range.Zero, lspDiag.Range);
        }

        [Theory]
        [InlineData("=1", 1)]
        [InlineData("1", 0)]
        public void Success_OnExpressionErrorWithSyntax(string syntaxData, int errorColumn)
        {
            var botDiag = new ExpressionError.Builder().Build();
            var botElement = CodeSerializer.Deserialize<BotElement>(syntaxData, new Uri("file:///c:/test.yml"));
            var lspDiag = botDiag.ToLspDiagnostics(botElement, new MarkResolver(TestData)).Single();
            Assert.Equal(Utils.CreateRange(0, errorColumn, 0, errorColumn), lspDiag.Range);
        }
    }
}
