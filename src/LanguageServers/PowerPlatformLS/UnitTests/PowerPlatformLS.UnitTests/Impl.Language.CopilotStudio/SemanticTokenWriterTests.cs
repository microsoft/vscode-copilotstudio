namespace Microsoft.PowerPlatformLS.UnitTests.Impl.Language.CopilotStudio
{
    using Microsoft.Agents.ObjectModel.Syntax;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Utilities;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.SemanticToken;
    using System.Linq;
    using Xunit;

    public class SemanticTokenWriterTests
    {
        [Fact]
        public void TestAddToken()
        {
            int start = 5;
            int length = 10;
            var type = SemanticTokenType.Keyword;
            var modifier = SemanticTokenModifier.Declaration;
            var tokenWriter = new SemanticTokenWriter(new MarkResolver(new string(' ', 100)));
            tokenWriter.Add(start, length, type, modifier);

            var bytes = tokenWriter.GetData();
            Assert.Equal(5, bytes.Length);
            Assert.Equal(0, bytes[0]); 
            Assert.Equal(start, bytes[1]);
            Assert.Equal(length, bytes[2]);
            Assert.Equal((int)type, bytes[3]);
            Assert.Equal((int)modifier, bytes[4]);
        }

        [Fact]
        public void TestAddMultiLineTokens()
        {
            var world = new World();
            var doc = world.AddFile("topic2.mcs.yml");
            var fileSyntax = doc.FileModel?.Syntax;

            var tokenWriter = new SemanticTokenWriter(doc.MarkResolver);
            if (fileSyntax != null)
            {
                var type = SemanticTokenType.Keyword;
                var modifier = SemanticTokenModifier.Declaration;
                SyntaxToken? syntaxToken = null;
                foreach (var syntaxNode in fileSyntax.EnumerateChildren(includeSelf: true, descendInto: (_) => false).ToArray())
                {
                    if (syntaxNode.IsList)
                    {
                        foreach( var token in syntaxNode.EnumerateTokens())
                        {
                            if (token.Value == "kind")
                            {
                                syntaxToken = token;
                                break;
                            }
                        }
                    }
                }

                if (syntaxToken != null)
                {
                    tokenWriter.Add(syntaxToken, type, modifier);

                    // { line: 0, startChar: 0, length: 4, tokenType: 0(keyword), tokenModifiers: 0(declaration) },
                    var kindProperty = new int[] { 0, 0, 4, (int)type, (int)modifier };
                    var tokenData = tokenWriter.GetData();
                    Assert.True(kindProperty.All(item => tokenData.Contains(item)));
                }
            }
        }
    }
}
