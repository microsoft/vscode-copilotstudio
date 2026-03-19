namespace Microsoft.Agents.ObjectModel.UnitTests.Yaml
{
    using Microsoft.Agents.ObjectModel.Syntax;
    using Microsoft.Agents.ObjectModel.Syntax.Text;
    using Microsoft.Agents.ObjectModel.Syntax.Tokens;
    using Microsoft.Agents.ObjectModel.Yaml;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio;
    using Xunit;

    public class SyntaxTokenExtensionsTests
    {
        // These positions are out of range and lookup should fail
        [Theory]
        [InlineData("kind")] // very start
        [InlineData("prompt")]
        [InlineData(": |")]
        [InlineData("later")]
        public void TestOutOfRange(string marker)
        {
            var resource = """
kind: Question
prompt: |
    012
    45
    67
later: abc
""";
            var token = GetToken(ref resource);

            Assert.Equal(SyntaxTokenKind.MultilineStringValue, token?.Kind);

            int filePosition = resource.IndexOf(marker);

            var ok = token!.TryMapFileOffsetToValueOffset(filePosition, out var offset);
            Assert.False(ok);
            Assert.Equal(-1, offset);
        }

        [Fact]
        public void TestMultiline()
        {
            var resource = """
kind: Question
prompt: |
    012
    45
    67
""";
            var token = GetToken(ref resource);

            Assert.Equal(SyntaxTokenKind.MultilineStringValue, token?.Kind);

            Assert.Equal("    012\n    45\n    67", token?.RawText);
            Assert.Equal("012\n45\n67", token?.Value);

            int rawOffset = resource.IndexOf('4');
            var ok = token!.TryMapFileOffsetToValueOffset(rawOffset, out var offset);
            Assert.True(ok);
            Assert.Equal(4, offset);

            ok = token!.TryMapValueOffsetToFileOffset(offset, out var fileOffset2);
            Assert.True(ok);
            Assert.Equal(rawOffset, fileOffset2);
        }

        // Ensure we can map duplex between value offset and file offset.
        [Fact]
        public void TestMultiline_DualMap()
        {
            var resource = """
kind: Question
prompt: |
    012
    
     def
    g
""";
            var token = GetToken(ref resource);

            Assert.Equal(SyntaxTokenKind.MultilineStringValue, token?.Kind);

            var tokenValue = token?.Value;
            TextSpan span = (TextSpan)token?.FullSpan!;

            for (int fileOffset = span.Start; fileOffset < span.End; fileOffset++)
            {
                var ch = resource[fileOffset];
                var ok = token.TryMapFileOffsetToValueOffset(fileOffset, out var offset);
                if (ok)
                {
                    Assert.True(ok);

                    ok = token.TryMapValueOffsetToFileOffset(offset, out var fileOffset2);
                    Assert.True(ok);
                    Assert.Equal(fileOffset, fileOffset2);
                }
                else
                {
                    // Whitepsace char
                    Assert.True(ch == ' ' || ch == '\r');
                }
            }
        }

        [Fact]
        public void TestUnquotedValue()
        {
            var resource = """
kind: Question
prompt: 012345
""";
            var token = GetToken(ref resource);

            Assert.Equal(SyntaxTokenKind.UnquotedValue, token?.Kind);

            Assert.Equal("[23..29)", token?.FullSpan.ToString());
            Assert.Equal(29, resource.Length);
            

            Assert.Equal("012345", token?.RawText);
            Assert.Equal("012345", token?.Value);

            int rawOffset = resource.IndexOf('4');

            Assert.Equal(27, rawOffset);

            var ok = token!.TryMapFileOffsetToValueOffset(rawOffset, out var offset);
            Assert.True(ok);
            Assert.Equal(4, offset);

            ok = token!.TryMapValueOffsetToFileOffset(offset, out var fileOffset2);
            Assert.True(ok);
            Assert.Equal(rawOffset, fileOffset2);
        }

        // Not yet supported.
        [Fact]
        public void TestQuotedValue()
        {
            var resource = """
kind: Question
prompt: "012345"
""";
            var token = GetToken(ref resource);

            Assert.Equal(SyntaxTokenKind.QuotedStringValue, token?.Kind);

            Assert.Equal("\"012345\"", token?.RawText);
            Assert.Equal("012345", token?.Value);

            int rawOffset = resource.IndexOf('4');
            var ok = token!.TryMapFileOffsetToValueOffset(rawOffset, out var offset);

            Assert.False(ok);
            Assert.Equal(-1, offset);

            ok = token!.TryMapValueOffsetToFileOffset(1, out var fileOffset2);
            Assert.False(ok);
            Assert.Equal(-1, fileOffset2);
        }

        // Helper to find the string token  within the resource. 
        // Find the the token - it should contain "12" in it. 
        static SyntaxToken? GetToken(ref string resource)
        {
            // Protect against git crlf adjustments.   Just use \n
            resource = resource.Replace("\r", "");

            var bot = CodeSerializer.Deserialize<BotElement>(resource);
            var syntax = (MappingObjectSyntax?)bot?.Syntax;

            int pos = resource.IndexOf("012");
            var token = (SyntaxToken?)syntax?.GetSyntaxNodeAtPosition(pos);

            return token;
        }

        // Aggressively unit test the mapping ability for multiline tokens. 
        [Theory]
        /* // rawoffset:
            //             012345 6 7890 12345
            var rawText = "  a c\r\n  d\n   ef";
            //               012  3   45   678
            // offset
        */
        /* 
             int[] expected = [
                -1,-1,0,1,2,-1,3,   // a c\r\n
                -1,-1,4,5,          // d\n
                -1,-1,6,7,8         //  ef    // leading whitespace
                ];
         */
        [InlineData("  a c\r\n  d\n   ef", 2, new int[] { -1, -1, 0, 1, 2, -1, 3, -1, -1, 4, 5, -1, -1, 6, 7, 8 })]
        /*
         // rawoffset:
            //             01234 56 78  9012 34567
            var rawText = "  a c\r\n\r\n  d\n   ef";
            //               012   3   4  56   789
            // offset
         */
        /*
            int[] expected = [
                -1,-1,0,1,2,-1,3,   // a c\r\n
                -1,4, // \r\n    // second break line
                -1,-1,5,6,          // d\n
                -1,-1,7,8,9         //  ef    // leading whitespace
            ];
         */
        [InlineData("  a c\r\n\r\n  d\n   ef", 2, new int[] { -1, -1, 0, 1, 2, -1, 3, -1, 4, -1, -1, 5, 6, -1, -1, 7, 8, 9 })]
        public void TryMapMultiline_Tests(string rawText, int indent, int[] expected)
        {
            // Interesting cases:
            // - inconsistent newlines between \r\n or just \n.
            // - totally blank lines.
            // - Yaml parser also ignores \r
            // - spaces in the value (outside of the indent). Either leading or in middle of value.
            // - escape chars

            for (int rawOffset = 0; rawOffset < rawText.Length; rawOffset++)
            {
                int expectedOffset = expected[rawOffset];

                var ok = MultilineValueOffsetMapper.MapMultilineRawOffsetToValueOffset(indent, rawText, rawOffset, out var offset);

                if (!ok)
                {
                    Assert.Equal(-1, offset);
                }

                Assert.Equal(expectedOffset, offset);
            }
        }

        // Ensure we have multi return lines in the mapping
        [Theory]
        [InlineData(1, 5, '1', true)]
        [InlineData(4, 8, '\n', true)]
        [InlineData(5, 9, '\n', true)]
        [InlineData(8, 16, 'e', true)]
        [InlineData(11, 23, 'g', true)]
        [InlineData(12, 0, '0', false)]
        public void TestMultiline_MultiReturnLines(int valueOffset, int expectedRawOffset, char expectedCharacter, bool expectedIsValidOffset)
        {
/*
    // There are 4 spaces of indent. Here is map between value offset and raw offset for template line:
    0: 4    // '0'
    1: 5    // '1'
    2: 6    // '2'
    3: 7    // '\n'
    4: 8    // '\n'
    5: 9    // '\n'
    6: 14   // ' '
    7: 15   // 'd'
    8: 16   // 'e'
    9: 17   // 'f'
    10: 18  // '\n'
    11: 23  // 'g'
*/

            var resource = """
kind: Question
prompt: |
    012


     def
    g
""";
            var token = GetToken(ref resource);

            Assert.Equal(SyntaxTokenKind.MultilineStringValue, token!.Kind);

            var mapper = new MultilineValueOffsetMapper(token);

            bool isValidOffset = mapper.TryMapValueOffsetToRawOffset(valueOffset, out int actualRawOffset);

            Assert.Equal(expectedIsValidOffset, isValidOffset);

            if (isValidOffset)
            {
                Assert.Equal(expectedRawOffset, actualRawOffset);
                Assert.Equal(expectedCharacter, resource[actualRawOffset + token.FullSpan.Start]);
            }
        }

        [Theory]
        // Normal case with same indent, no extra space and no trailing break lines.
        [InlineData("kind: Question\r\nprompt: |+\r\n    012\r\n\r\n\r\n    def\r\n    g")]
        [InlineData("kind: Question\r\nprompt: |+\r\n    012\r\n\r\n\r\n    def\r\n    g\r\n")]
        [InlineData("kind: Question\r\nprompt: |-\r\n    012\r\n\r\n\r\n    def\r\n    g")]
        [InlineData("kind: Question\r\nprompt: |-\r\n    012\r\n\r\n\r\n    def\r\n    g\r\n")]
        [InlineData("kind: Question\r\nprompt: |\r\n    012\r\n\r\n\r\n    def\r\n    g")]
        [InlineData("kind: Question\r\nprompt: |\r\n    012\r\n\r\n\r\n    def\r\n    g\r\n")]

        // The following tests will test if MultilineValueOffsetMapper fail with "Index was outside the bounds of the array" if we don't initialize _mapValueToRaw correctly.
        // Token RawText has trailing break lines.
        [InlineData("kind: Question\r\nprompt: |+\r\n    012\r\n\r\n\r\n    def\r\n    g\r\n    \r\n\r\n")]
        [InlineData("kind: Question\r\nprompt: |+\r\n    012\r\n   \r\n                  \r\n    def\r\n    g\r\n  \r\n                 \r\n   \r\n")]
        [InlineData("kind: Question\r\nprompt: |+\r\n    012\r\n    def\r\n    g\r\n\r\n\r\n")]
        [InlineData("kind: Question\r\nprompt: |+\r\n    \r\n              \r\n    012\r\n    def\r\n    g\r\n\r\n\r\n")]
        
        // The following tests will test if MultilineValueOffsetMapper fail with "Index was outside the bounds of the array" if we don't initialize _mapValueToRaw correctly.
        [InlineData("kind: Question\r\nprompt: |-\r\n    012\r\n\r\n\r\n    def\r\n    g\r\n    \r\n\r\n")]
        [InlineData("kind: Question\r\nprompt: |-\r\n    012\r\n\r\n\r\n    def\r\n    g\r\n  \r\n                 \r\n   \r\n")]
        [InlineData("kind: Question\r\nprompt: |-\r\n    012\r\n    def\r\n    g\r\n\r\n\r\n")]
        [InlineData("kind: Question\r\nprompt: |-\r\n    \r\n              \r\n    012\r\n    def\r\n    g\r\n\r\n\r\n")]

        // The following tests will test if MultilineValueOffsetMapper fail with "Index was outside the bounds of the array" if we don't initialize _mapValueToRaw correctly.
        [InlineData("kind: Question\r\nprompt: |\r\n    012\r\n\r\n\r\n    def\r\n    g\r\n    \r\n\r\n")]
        [InlineData("kind: Question\r\nprompt: |\r\n    012\r\n\r\n\r\n    def\r\n    g\r\n  \r\n                 \r\n   \r\n")]
        [InlineData("kind: Question\r\nprompt: |\r\n    012\r\n    def\r\n    g\r\n\r\n\r\n")]
        [InlineData("kind: Question\r\nprompt: |\r\n    \r\n              \r\n    012\r\n    def\r\n    g\r\n\r\n\r\n")]
        // The following test will test if mapping offset map wrongly from space to \n or from \n to space when we have additional space after indent.
        [InlineData("kind: Question\r\nprompt: |\r\n    012\r\n\r\n\r\n       def\r\n     g\r\n")]

        // Preceding break lines
        [InlineData("kind: Question\r\nprompt: |+\r\n\r\n\r\n    012\r\n\r\n\r\n    def\r\n    g\r\n")]
        [InlineData("kind: Question\r\nprompt: |-\r\n\r\n\r\n    012\r\n\r\n\r\n    def\r\n    g\r\n")]
        [InlineData("kind: Question\r\nprompt: |\r\n\r\n\r\n    012\r\n\r\n\r\n    def\r\n    g\r\n")]
        [InlineData("kind: Question\r\nprompt: |+\r\n   \r\n         \r\n    012\r\n  \r\n              \r\n    def\r\n    g\r\n")]
        [InlineData("kind: Question\r\nprompt: |-\r\n   \r\n    \r\n    012\r\n  \r\n              \r\n    def\r\n    g\r\n")]
        [InlineData("kind: Question\r\nprompt: |\r\n    \r\n                        \r\n    012\r\n  \r\n              \r\n    def\r\n    g\r\n")]

        // Preceding and trailing break lines
        [InlineData("kind: Question\r\nprompt: |+\r\n    \r\n    \r\n    012\r\n  \r\n              \r\n    def\r\n    g\r\n    \r\n              \r\n")]
        [InlineData("kind: Question\r\nprompt: |-\r\n    \r\n    \r\n    012\r\n  \r\n              \r\n    def\r\n    g\r\n    \r\n              \r\n")]
        [InlineData("kind: Question\r\nprompt: |\r\n    \r\n    \r\n    012\r\n  \r\n              \r\n    def\r\n    g\r\n    \r\n              \r\n")]

        public void SyntaxToken_OffsetMapper(string resource)
        {
            string[] lineEndings = { "\r\n", "\n" };
            string[] tokenEndings = { "", "id:12ab\r\n" }; // "": multiline token is at the end, "id:12ab\r\n": multiline token is not at the end.

            foreach (var tokenEnding in tokenEndings)
            {
                foreach (var newline in lineEndings)
                {
                    string normalizedTokenEnding = newline == "\n" ? tokenEnding.Replace("\r\n", "\n") : tokenEnding;
                    string normalizedResource = (newline == "\n" ? resource.Replace("\r\n", "\n") : resource) + normalizedTokenEnding;

                    var token = GetToken(ref normalizedResource);

                    // ! : test
                    Assert.Equal(SyntaxTokenKind.MultilineStringValue, token!.Kind);

                    var mapper = new MultilineValueOffsetMapper(token);

                    // ! value: not null
                    for (int valueOffset = 0; valueOffset < token.Value!.Length; valueOffset++)
                    {
                        bool isValidOffset = mapper.TryMapValueOffsetToRawOffset(valueOffset, out int actualRawOffset);

                        Assert.True(isValidOffset);
                        var characterInValue = token.Value[valueOffset];
                        var actualCharacterInRawText = normalizedResource[actualRawOffset + token.FullSpan.Start];

                        Assert.Equal(characterInValue, actualCharacterInRawText);
                    }
                }
            }
        }
    }
}
