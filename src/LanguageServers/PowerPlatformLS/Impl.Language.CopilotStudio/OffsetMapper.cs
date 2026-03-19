namespace Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio
{
    using Microsoft.Agents.ObjectModel.Syntax.Tokens;
    using Microsoft.Agents.ObjectModel.Syntax;
    using System;
    using System.Diagnostics;

    internal static class SyntaxTokenExtensions
    {
        // Get the appropriate mapper for this kind of string literal.
        // Reusing the mapper can be useful when you have many lookups to perform.
        public static OffsetMapper GetOffsetMapper(this SyntaxToken token) => token.Kind switch
        {
            SyntaxTokenKind.UnquotedValue => new UnquotedValueOffsetMapper(token),
            SyntaxTokenKind.MultilineStringValue => new MultilineValueOffsetMapper(token),
            SyntaxTokenKind.QuotedStringValue => new QuotedValueOffsetMapper(token),
            _ => throw new InvalidOperationException($"Unsupported string kind: {token.Kind}"),// This is a bug.
        };

        public static bool TryMapValueOffsetToFileOffset(
            this SyntaxToken token,
            int valueOffset,
            out int fileOffset)
        {
            var mapper = token.GetOffsetMapper();
            return mapper.TryMapValueOffsetToFileOffset(valueOffset, out fileOffset);
        }

        /// <summary>
        /// Map from an offset in <see cref="SyntaxToken.RawText"/> to the corresponding
        /// offset in <see cref="SyntaxToken.Value"/>.
        /// </summary>
        /// <param name="token">the token. Must be a text. </param>
        /// <param name="fileOffset">Offset within file.</param>
        /// <param name="valueOffset">A corresponding offset within token.Value.</param>
        /// <returns>true if mapping is possible. False if we can't map (such as if the raw offset is in leading whitespace that doesn't map to a value)</returns>
        public static bool TryMapFileOffsetToValueOffset(
            this SyntaxToken token,
            int fileOffset,
            out int valueOffset)
        {
            var mapper = token.GetOffsetMapper();
            return mapper.TryMapFileOffsetToValueOffset(fileOffset, out valueOffset);
        }
    }

    // Mapping between RawOffset/ValueOFfset within a Yaml string.
    // Derived classes can handle different Yaml string kinds.
    //
    // For mapping, we have 3 concepts for SyntaxToken.
    // Never use just "offset". Be specific: 
    // 1. "FileOffset" - position in overall file.  Starts at token.FullSpan.Start.
    // 2. "RawOffset" - position within "token.RawText".  Starts at 0. Includes whitespace indents, \r, and char escape codes.  Same characters as fileoffset, but relative to the token, so 0 is token.FullSpan.Start. 
    // 3. "Value Offset" - position within "token.Value". Start at 0. This is the logical value. No indents, normalized to only \n, char escapes have been evaluated. 
    //
    // Mapping between (1) and (2) is easy since we just adjust by token.FullSpan.Start.
    // Mapping between (2) and (3) is hard since we need to handle whitespace indents, escape chars, \r getting removed, etc.  
    // We frequently need to map between Power Fx Values (1) and LSP file diagnostics (3). 
    internal abstract class OffsetMapper
    {
        protected readonly string _tokenText;
        private readonly int _startOffset;

        private protected OffsetMapper(SyntaxToken syntaxToken)
            : this(syntaxToken.RawText ?? throw new ArgumentNullException(nameof(syntaxToken.RawText)), syntaxToken.Position)
        {
        }

        private protected OffsetMapper(string tokenText, int startOffset)
        {
            _tokenText = tokenText;
            _startOffset = startOffset;
        }

        public abstract bool TryMapValueOffsetToRawOffset(int valueOffset, out int rawOffset);

        public bool TryMapValueOffsetToFileOffset(int valueOffset, out int fileOffset)
        {
            if (TryMapValueOffsetToRawOffset(valueOffset, out var rawOffset))
            {
                fileOffset = _startOffset + rawOffset;
                return true;
            }

            fileOffset = -1;
            return false;
        }

        public bool TryMapFileOffsetToValueOffset(int fileOffset, out int valueOffset)
        {
            string? rawText = _tokenText;
            if (rawText != null)
            {
                int rawOffset = fileOffset - _startOffset;

                if (rawOffset >= 0 && rawOffset <= rawText.Length)
                {
                    var result = TryMapRawOffsetToValueOffset(rawOffset, rawText, out valueOffset);
                    return result;
                }
            }
            valueOffset = -1;
            return false;
        }

        // Caller ensures rawOffset is within rawText string. 
        protected abstract bool TryMapRawOffsetToValueOffset(int rawOffset, string rawText, out int valueOffset);

        // These chars never appear in output Token.Value, so they
        // can't contribute to the offset. 
        protected static bool IsIgnoreChar(char ch)
        {
            return (ch == '\r');
        }

        // Get leading whitespace index for a multiline token.
        // Normally, this is the whitespace on the first line.
        protected int GetWhitespaceIndent()
        {
            // Ideally - this would be a property on SyntaxToken captured by the lexer
            // when we created the token (and lexer would then handle advanced scenarios like "whitespace indicators".) 

            // ! Caller has ensures we have a value
            var tokenText = _tokenText.AsSpan();

            if (tokenText.Length > 0 && tokenText[0] == ' ')
            {
                return tokenText.IndexOfAnyExcept(' ');
            }

            // Indent should be counted from last preceding break line to first non white space character if it starts with non space.
            var firstNonWhiteSpace = tokenText.IndexOfAnyExcept('\n', ' ');

            if (firstNonWhiteSpace > 0)
            {
                var lastBreakLine = tokenText[..firstNonWhiteSpace].LastIndexOf('\n');
                return lastBreakLine == -1 ? firstNonWhiteSpace : firstNonWhiteSpace - 1 - lastBreakLine;
            }

            return 0;
        }

        // Get number of trailing white space that appear consecutively at the end of a multiline token.
        protected int GetTrailingWhitespace()
        {
            var tokenText = _tokenText.AsSpan();
            int lastNonWhiteSpace = tokenText.LastIndexOfAnyExcept('\r', '\n', ' ');
            return lastNonWhiteSpace == -1 ? tokenText.Length : tokenText.Length - 1 - lastNonWhiteSpace;
        }
    }

    // Unquoted strings are the same string. Starts as first non-whitepsace after the ':'.
    // No quotes, no multiline. 
    // prop: value 
    internal class UnquotedValueOffsetMapper : OffsetMapper
    {
        internal UnquotedValueOffsetMapper(SyntaxToken syntaxToken)
            : base(syntaxToken)
        {
            Debug.Assert(syntaxToken.Kind == SyntaxTokenKind.UnquotedValue);
        }

        internal UnquotedValueOffsetMapper(string text, int startOffset) : base(text, startOffset) { }

        public override bool TryMapValueOffsetToRawOffset(int valueOffset, out int rawOffset)
        {
            rawOffset = valueOffset;
            return true;
        }

        protected override bool TryMapRawOffsetToValueOffset(int rawOffset, string rawText, out int valueOffset)
        {
            valueOffset = rawOffset;
            return true;
        }
    }

    // Quotes are like class json strings. They are in quotes, and have escape characters:
    // prop: "ab\nc"
    internal class QuotedValueOffsetMapper : OffsetMapper
    {
        internal QuotedValueOffsetMapper(SyntaxToken syntaxToken)
            : base(syntaxToken)
        {
            Debug.Assert(syntaxToken.Kind == SyntaxTokenKind.QuotedStringValue);
        }

        public override bool TryMapValueOffsetToRawOffset(int valueOffset, out int rawOffset)
        {
            // Not implemented.
            rawOffset = -1;
            return false;
        }

        protected override bool TryMapRawOffsetToValueOffset(int rawOffset, string rawText, out int valueOffset)
        {
            // Not implemented.
            valueOffset = -1;
            return false;
        }
    }

    // Yaml Multiline is complex.
    // https://stackoverflow.com/a/21699210/534514 
    internal class MultilineValueOffsetMapper : OffsetMapper
    {
        // cache the results for Value-->File/Raw because:
        // - computing them requires a O[n] scan through the raw text
        // - we expected to get called many times for each value. We may have 100s of power fx tokens that
        //   need to get mapped back to file positions. So we need a fast O[1] lookup.
        //
        // Whereas File-->VAlue is a rare operation happening per-user gesture, and so O[1] is ok.
        private readonly int[] _mapValueToRaw;

        internal MultilineValueOffsetMapper(SyntaxToken syntaxToken)
            : base(syntaxToken)
        {
            Debug.Assert(syntaxToken.Kind == SyntaxTokenKind.MultilineStringValue);

            string? rawText = syntaxToken.RawText;
            string? valueStr = syntaxToken.Value;

            if (rawText == null || valueStr == null)
            {
                _mapValueToRaw = [];
                return;
            }

            int indent = GetWhitespaceIndent();

            // valueStr does not have the same trailing break lines with rawText in case of | and |-.
            // To avoid out of bound, we set array size is valueStr and number of trailing break lines in rawText.
            _mapValueToRaw = new int[valueStr.Length + GetTrailingWhitespace()];

            int valueOffset = 0;
            bool isBreakLineBefore = false;
            for (int i = 0, iCol = 0; i < rawText.Length; i++, iCol++)
            {
                char ch = rawText[i];

                if ((i == 0 && ch == '\n') || (isBreakLineBefore && (IsIgnoreChar(ch) || ch == '\n')))
                {
                    iCol = indent;
                }

                if (iCol >= indent && !IsIgnoreChar(ch))
                {                    
                    _mapValueToRaw[valueOffset++] = i;
                }

                isBreakLineBefore = ch == '\n' || (isBreakLineBefore && char.IsWhiteSpace(ch));

                if (ch == '\n')
                {
                    iCol = -1;
                }
            }
        }

        public override bool TryMapValueOffsetToRawOffset(int valueOffset, out int rawOffset)
        {
            if (valueOffset >= 0 && valueOffset < _mapValueToRaw.Length)
            {
                rawOffset = _mapValueToRaw[valueOffset];
                return true;
            }

            rawOffset = -1;
            return false;
        }

        protected override bool TryMapRawOffsetToValueOffset(int rawOffset, string rawText, out int valueOffset)
        {
            int indent = GetWhitespaceIndent();

            return MapMultilineRawOffsetToValueOffset(indent, rawText, rawOffset, out valueOffset);
        }

        // Handle mapping for SyntaxTokenKind.MultilineStringValue
        // - No quotes or escape characters
        // - but has significant whitespace.
        //
        // This is literally mimicking the logic used to build YamlReader._valueBuilder
        // for multiline literals. 
        internal static bool MapMultilineRawOffsetToValueOffset(
            int indent, // number of characters to indent. 
            string rawText,
            int rawTextOffset,
            out int valueOffset)
        {
            // RawText will be like:
            // <indent><line1>\n<indent><line2>

            // And map to an offset in:
            // <line1>\n<line2>

            // Note that:
            // - <indent> is fixed length for all lines, length={indent}.
            // - lineN may start with whitespace characters.
            // - lineN may be empty, but we still have <indent>
            // - \n is newline separator. 
            // - \r are excluded from token.Value, so ignore them. 
            // - offset can map to \n and \r chars. We just skip the <indent>

            if (rawTextOffset < rawText.Length && IsIgnoreChar(rawText[rawTextOffset]))
            {
                valueOffset = -1;
                return false;
            }

            valueOffset = 0; // start before first char
            int iCol = 0;
            bool isBreakLineBefore = false;

            for (int i = 0; i < rawTextOffset; i++, iCol++)
            {
                var ch = rawText[i];

                if ((i == 0 && ch == '\n') || (isBreakLineBefore && (IsIgnoreChar(ch) || ch == '\n')))
                {
                    iCol = indent;
                }

                if (iCol >= indent && !IsIgnoreChar(ch))
                {
                    valueOffset++;
                }

                isBreakLineBefore = ch == '\n' || (isBreakLineBefore && char.IsWhiteSpace(ch));

                if (ch == '\n')
                {
                    iCol = -1;
                }
            }

            bool isInIndent = iCol < indent;
            if (isInIndent)
            {
                valueOffset = -1;
                return false;
            }
            return true;
        }
    }
}
