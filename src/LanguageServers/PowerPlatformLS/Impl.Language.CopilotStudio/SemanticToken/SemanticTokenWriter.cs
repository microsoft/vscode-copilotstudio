namespace Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.SemanticToken
{
    using Microsoft.Agents.ObjectModel.Syntax;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Utilities;

    internal class SemanticTokenWriter
    {
        private readonly List<int> _semanticTokenData = new List<int>();
        private readonly MarkResolver _markResolver;
        private Position _lastPosition = default;

        public SemanticTokenWriter(MarkResolver markResolver)
        {
            _markResolver = markResolver;
        }

        // Handle single value or multi-line strings and add token to the writer
        // Use this with TrackLineBreaks to update deltas for line break.
        public void Add(SyntaxToken token, SemanticTokenType type, SemanticTokenModifier modifier)
        {
            Add(token.Position, token.FullWidth, type, modifier);
        }

        // Override the Add method to add tokens to _semanticTokenData.
        // Add will add semantic token for same line if _deltaLine is 0 and next line if _deltaLine is 1.
        // Use this with TrackLineBreaks to update deltas for line break.
        public void Add(int start, int length, SemanticTokenType type, SemanticTokenModifier modifier)
        {
            if (length == 0)
            {
                throw new ArgumentException("Emitting 0 length token");
            }

            foreach (var (newPosition, lengthInLine) in _markResolver.GetRangesInLines(start, length))
            {
                if (lengthInLine == 0)
                {
                    continue;
                }

                var deltaLine = newPosition.Line - _lastPosition.Line;
                var deltaStart = deltaLine > 0 ? newPosition.Character : newPosition.Character - _lastPosition.Character;
                _semanticTokenData.AddRange([deltaLine, deltaStart, lengthInLine, (int)type, (int)modifier]);
                _lastPosition = newPosition;
            }
        }

        // Override GetBytes to return the token data
        public int[] GetData()
        {
            return _semanticTokenData.ToArray();
        }

    }
}