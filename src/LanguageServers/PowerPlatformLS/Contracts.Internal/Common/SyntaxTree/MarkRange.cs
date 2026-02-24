namespace Microsoft.PowerPlatformLS.Contracts.Internal.Common.SyntaxTree
{
    using System.Diagnostics;

    // This maps to existing Lsp source trackers:
    //  MarkRange --> Contracts.Lsp.Models.Range
    //  Range --> Contracts.Lsp.Models.Position
    [DebuggerDisplay("{Start}-{End}")]
    public class MarkRange
    {
        public Mark Start { get; }
        public Mark End { get; }

        public MarkRange(Mark start, Mark end)
        {
            Start = start;
            End = end;
        }

        public MarkRange(int startLine, int startColumn, int endLine, int endColumn)
        {
            Start = new Mark() { Line = startLine, Column = startColumn };
            End = new Mark() { Line = endLine, Column = endColumn };
        }

        public Contracts.Lsp.Models.Range ToLspRange()
        {
            var startLineIdx = (int)Start.Line - 1;
            var startColumnIdx = (int)Start.Column - 1;
            var endLineIdx = (int)End.Line - 1;
            var endColumnIdx = (int)End.Column - 1;

            return new Contracts.Lsp.Models.Range()
            {
                Start = new Contracts.Lsp.Models.Position
                {
                    Line = startLineIdx,
                    Character = startColumnIdx
                },
                End = new Contracts.Lsp.Models.Position
                {
                    Line = endLineIdx,
                    Character = endColumnIdx
                }
            };
        }
    }
}
