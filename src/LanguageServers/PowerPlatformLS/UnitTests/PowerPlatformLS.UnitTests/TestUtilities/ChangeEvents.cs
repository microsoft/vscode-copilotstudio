namespace Microsoft.PowerPlatformLS.UnitTests.TestUtilities
{
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;

    internal static class ChangeEvents
    {
        public static TextDocumentChangeEvent InsertTextAt(string text, int line, int character)
        {
            return new TextDocumentChangeEvent
            {
                Range = new Range
                {
                    Start = new Position
                    {
                        Line = line,
                        Character = character,
                    },
                    End = new Position
                    {
                        Line = line,
                        Character = character,
                    }
                },
                Text = text,
            };
        }

        public static TextDocumentChangeEvent EraseCharacterAt(int line, int character)
        {
            return new TextDocumentChangeEvent
            {
                Range = new Range
                {
                    Start = new Position
                    {
                        Line = line,
                        Character = character,
                    },
                    End = new Position
                    {
                        Line = line,
                        Character = character + 1,
                    }
                },
                Text = string.Empty,
            };
        }
    }
}
