namespace Microsoft.PowerPlatformLS.Contracts.Internal.UnitTests
{
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models.Lsp;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using System;
    using Xunit;
    using Range = Lsp.Models.Range;

    public class LspDocumentTests
    {
        [Fact]
        public void AlteredText_OnApplyChanges()
        {
            const string TestContent = "In coded realms where logic resides, \nMachines hum softly, shaping our tides.";
            var document = new TestLspDocument(
                new FilePath($"c:/{nameof(AlteredText_OnApplyChanges)}"),
                TestContent,
                Constants.LanguageIds.CopilotStudio,
                new DirectoryPath("c:/"));

            var changes = new TextDocumentChangeEvent[]
            {
                new()
                {
                    Range = new Range
                    {
                        Start = new Position { Line = 1, Character = 0 },
                        End = new Position { Line = 1, Character = 19 }
                    },
                    Text = "Machines pulse brightly"
                },
                new()
                {
                    Range = new Range
                    {
                        Start = new Position { Line = 1, Character = 23 },
                        End = new Position { Line = 1, Character = 43 }
                    },
                    Text = " as progress abides."
                }
            };
            document.ApplyChanges(changes);

            const string ExpectedResult = "In coded realms where logic resides, \nMachines pulse brightly as progress abides.";
            Assert.Equal(ExpectedResult, document.Text);
        }

        private class TestLspDocument : LspDocument
        {
            public TestLspDocument(FilePath path, string text, string languageId, DirectoryPath directoryPath) : base(path, text, languageId, directoryPath)
            {
            }
        }
    }
}