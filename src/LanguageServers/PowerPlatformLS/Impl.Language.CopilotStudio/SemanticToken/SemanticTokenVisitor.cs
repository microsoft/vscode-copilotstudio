namespace Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.SemanticToken
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.Agents.ObjectModel.Syntax;
    using Microsoft.Agents.ObjectModel.Syntax.Tokens;
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.PowerFx.Syntax;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Completion;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Models;

    // The visitor class for handling syntax nodes and semantic tokens
    internal class SemanticTokenVisitor
    {
        public IReadOnlyList<int> SemanticTokenData => _semanticTokenWriter.GetData();

        private readonly RequestContext _requestContext;
        private readonly SemanticTokenWriter _semanticTokenWriter;
        private readonly ILspLogger _logger;

        public SemanticTokenVisitor(RequestContext requestContext, ILspLogger logger)
        {
            _requestContext = requestContext;
            _semanticTokenWriter = new SemanticTokenWriter(requestContext.Document.MarkResolver);
            _logger = logger;
        }

        public void Visit(SyntaxNode node)
        {
            foreach (var token in node.EnumerateTokens())
            {
                Visit(token);
            }
        }

        private void Visit(SyntaxToken token)
        {
            if (token.FullWidth == 0 || token.Kind is SyntaxTokenKind.CarriageReturnLineFeed
                or SyntaxTokenKind.LineFeed
                or SyntaxTokenKind.Indent
                or SyntaxTokenKind.Whitespace)
            {
                return;
            }
            
            // write semantics for syntax tokens
            // some tokens are composites, such as Power Fx expressions and dialog references
            // others are handled by emitting a token for a single element
            var semanticTokenModifier = SemanticTokenModifier.Declaration;

            if (token.Kind is SyntaxTokenKind.MultiLineStringStart)
            {
                _semanticTokenWriter.Add(token, SemanticTokenType.Keyword, semanticTokenModifier);
                return;
            }

            var semanticElement = token.GetElement();
            if (semanticElement is ExpressionBase expr)
            {
                if (expr.IsExpression)
                {
                    TokenizePowerFxExpression(token, expr, expr.ExpressionText);
                    return;
                }
                else if (expr.IsVariableReference)
                {
                    // =Topic.Foo.Bar
                    _semanticTokenWriter.Add(token.Position, 1, SemanticTokenType.Operator, SemanticTokenModifier.Static);
                    TokenizePropertyPath(token, token.Position + 1, expr.VariableReference);
                    return;
                }
                else if (expr is DialogExpression dlg)
                {
                    TokenizeIdentifier(token, dlg.LiteralValue);
                    return;
                }
            }
            else if (semanticElement is TemplateLine templateLine)
            {
                TokenizeTemplateLine(token, templateLine);
                return;
            }

            object? semanticPropertyValue = null;
            if (semanticElement.Syntax != token && token.Value != null
                && token.Kind is SyntaxTokenKind.QuotedStringValue or SyntaxTokenKind.UnquotedValue or SyntaxTokenKind.MultilineStringValue
                && token.Parent is IMappingKeyValueSyntax kvp && kvp.PropertyName.Value is not null)
            {
                semanticPropertyValue = BotElementReflection.GetPropertyValueOrNull(semanticElement, kvp.PropertyName.Value);
            }

            if (semanticPropertyValue is InitializablePropertyPath initPath)
            {
                TokenizePropertyPath(token, initPath);
                return;
            }

            if (semanticPropertyValue is PropertyPath propertyPath)
            {
                TokenizePropertyPath(token, propertyPath);
                return;
            }

            if (semanticPropertyValue is IIdentifier identifier)
            {
                TokenizeIdentifier(token, identifier);
                return;
            }

            SemanticTokenType semanticTokenType = SemanticTokenType.Default;

            if (semanticPropertyValue is EnumWrapper or BotElementKind or EntityReference)
            {
                semanticTokenType = SemanticTokenType.EnumMember;
            }


            if (semanticTokenType == SemanticTokenType.Default)
            {
                semanticTokenType = semanticElement.Kind switch
                {
                    BotElementKind.BoolExpression or BotElementKind.ValueExpression when bool.TryParse(token.Value, out _) => SemanticTokenType.Keyword,
                    BotElementKind.ValueExpression when double.TryParse(token.Value, out _) => SemanticTokenType.Number,
                    BotElementKind.EnumExpression_T => SemanticTokenType.Enum,
                    _ => SemanticTokenType.Default,
                };
            }

            if (semanticTokenType == SemanticTokenType.Default)
            {
                semanticTokenType = token.Kind switch
                {
                    SyntaxTokenKind.PropertyName => SemanticTokenType.Property,
                    SyntaxTokenKind.UnquotedValue when double.TryParse(token.Value, out _) => SemanticTokenType.Number,
                    SyntaxTokenKind.UnquotedValue when bool.TryParse(token.Value, out _) => SemanticTokenType.Keyword,
                    SyntaxTokenKind.UnquotedValue or SyntaxTokenKind.QuotedStringValue => SemanticTokenType.String,
                    SyntaxTokenKind.MultiLineStringStart or SyntaxTokenKind.KeyValueSeparator or SyntaxTokenKind.SequenceSeparator => SemanticTokenType.Operator,
                    SyntaxTokenKind.Comment => SemanticTokenType.Comment,
                    _ => SemanticTokenType.Default,
                };
            }

            if (semanticTokenType != SemanticTokenType.Default)
            {
                _semanticTokenWriter.Add(token, semanticTokenType, semanticTokenModifier);
            }
        }

        private void TokenizeIdentifier(SyntaxToken token, IIdentifier identifier)
        {
            TokenizeDottedString(token, identifier.ToString(), token.Position, SemanticTokenType.Namespace, SemanticTokenType.Namespace, SemanticTokenType.EnumMember, SemanticTokenModifier.Readonly);
        }

        private int TokenizePropertyPath(SyntaxToken token, InitializablePropertyPath propertyPath)
        {
            int startOffset = token.Position;
            if (propertyPath.IsInitializer)
            {
                _semanticTokenWriter.Add(startOffset, 4, SemanticTokenType.Keyword, SemanticTokenModifier.Definition);
                startOffset += 4;

                _semanticTokenWriter.Add(startOffset, 1, SemanticTokenType.Operator, SemanticTokenModifier.Static);
                startOffset++;
            }

            return startOffset + TokenizePropertyPath(token, startOffset, propertyPath.Path);
        }

        private int TokenizePropertyPath(SyntaxToken token, int startOffset, PropertyPath propertyPath)
        {
            var initialTokenType = propertyPath.IsValid ? SemanticTokenType.Keyword : SemanticTokenType.Property;
            var path = propertyPath.ToString();
            TokenizeDottedString(token, path, startOffset, initialTokenType, initialTokenType, SemanticTokenType.Property, SemanticTokenModifier.Modification);
            return path.Length;
        }

        private void TokenizeDottedString(SyntaxToken token, string value, int startOffset, SemanticTokenType initialTokenType, SemanticTokenType groupType, SemanticTokenType leafType, SemanticTokenModifier modifiers)
        {
            int offset = startOffset;
            ReadOnlySpan<char> remainingStr = value;
            while (remainingStr.Length > 0)
            {
                var indexOfDot = remainingStr.IndexOf('.');
                if (indexOfDot < 0)
                {
                   _semanticTokenWriter.Add(offset, remainingStr.Length, leafType, modifiers);
                    offset += remainingStr.Length;
                    remainingStr = ReadOnlySpan<char>.Empty;
                }
                else
                {
                    if (indexOfDot > 0)
                    {
                        _semanticTokenWriter.Add(offset, indexOfDot, groupType, modifiers);
                        offset += indexOfDot;
                    }

                    _semanticTokenWriter.Add(offset, 1, SemanticTokenType.Operator, SemanticTokenModifier.Static);
                    offset++;
                    remainingStr = remainingStr.Slice(indexOfDot + 1);
                }
            }
        }

        private void TokenizeTemplateLine(SyntaxToken token, TemplateLine templateLine)
        {
            foreach (var item in templateLine.GetSegmentsWithSpans())
            {
                var currentSpan = item.rawOffsetSpan;
                var segment = item.segment;

                var spanStart = currentSpan.Start;
                var textLength = currentSpan.Length;
                if (segment is ExpressionSegment exprSegment)
                {
                    // ! expression is not null
                    var expressionElement = exprSegment.Expression!;
                    if (expressionElement.IsVariableReference)
                    {
                        // {formula}
                        // opening brace {
                        _semanticTokenWriter.Add(spanStart, 1, SemanticTokenType.Keyword, SemanticTokenModifier.Declaration);
                        TokenizePropertyPath(token, spanStart + 1, expressionElement.VariableReference);
                    }
                    else if (expressionElement.IsExpression)
                    {
                        var offsetMapper = new UnquotedValueOffsetMapper(expressionElement.ExpressionText, spanStart);
                        TokenizePowerFxExpression(offsetMapper, expressionElement, expressionElement.ExpressionText);
                    }

                    // closing brace }
                    _semanticTokenWriter.Add(spanStart + textLength - 1, 1, SemanticTokenType.Keyword, SemanticTokenModifier.Declaration);
                }
                else if (segment is TextSegment text)
                {                                    
                    _semanticTokenWriter.Add(spanStart, textLength, SemanticTokenType.String, SemanticTokenModifier.Declaration);
                }
                else
                {
                    throw new InvalidOperationException("Unknown segment");
                }
            }
        }


        public void TokenizePowerFxExpression(SyntaxToken syntaxToken, ExpressionBase expr, string exprText)
        {
            var mapper = syntaxToken.GetOffsetMapper();
            TokenizePowerFxExpression(mapper, expr, exprText);
        }

        // Tokenize a Power Fx expression
        // Caller has confirmed IsExpression=true,
        public void TokenizePowerFxExpression(OffsetMapper offsetMapper, ExpressionBase expr, string exprText)
        {
            const SemanticTokenModifier Modifier = SemanticTokenModifier.Declaration;

            if (offsetMapper.TryMapValueOffsetToFileOffset(0, out int offset))
            {
                // First char is '=' or '{' not part of expression. Color that differently.
                _semanticTokenWriter.Add(offset, 1, SemanticTokenType.Keyword, Modifier);
            }

            if (!expr.TryGetCheckResult(_requestContext, out var check, out var engine))
            {
                return;
            }

            // TODO - use the CheckResult to get a semantic aware tokenization. 
            var fxTokens = engine.Tokenize(exprText);

            // We require that tokens are in sorted order (Because line breaks can only go forward, not backwards).
            foreach (var fxToken in fxTokens)
            {
                if (fxToken.Kind == TokKind.Whitespace)
                {
                    // Don't colorize whitespace. 
                    continue;
                }

                if (offsetMapper.TryMapValueOffsetToFileOffset(fxToken.Span.Min, out var position)
                    && offsetMapper.TryMapValueOffsetToFileOffset(fxToken.Span.Lim, out var fileEndPosition))
                {
                    int length = fileEndPosition - position;
                    SemanticTokenType tokenType = GetTokenColor(fxToken.Kind);
                    // shift by 1 to account for the '=' or '{' at the start of the expression
                    _semanticTokenWriter.Add(position + 1, length, tokenType, Modifier);
                }
            }
        }

        // Map Power Fx tokens to a LSP token color. 
        SemanticTokenType GetTokenColor(TokKind kind) => kind switch
        {
            TokKind.Ident => SemanticTokenType.Property, // todo improve
            TokKind.NumLit or TokKind.DecLit => SemanticTokenType.Number,

            TokKind.StrInterpStart or
            TokKind.StrInterpEnd or
            TokKind.StrLit => SemanticTokenType.String,
            TokKind.Comment => SemanticTokenType.Comment,

            TokKind.Add or
            TokKind.Sub or
            TokKind.Mul or
            TokKind.Div or
            TokKind.Equ or
            TokKind.Lss or
            TokKind.LssEqu or
            TokKind.Grt or
            TokKind.GrtEqu or
            TokKind.LssGrt or
            TokKind.Comma or
            TokKind.Dot or
            TokKind.Colon or
            TokKind.Ampersand or
            TokKind.PercentSign or
            TokKind.Or or
            TokKind.And or
            TokKind.Bang or
            TokKind.At or
            TokKind.ColonEqual or
            TokKind.Caret => SemanticTokenType.Operator,

            TokKind.Semicolon or
            TokKind.ParenOpen or
            TokKind.ParenClose or
            TokKind.CurlyOpen or
            TokKind.CurlyClose or
            TokKind.BracketOpen or
            TokKind.True or
            TokKind.False or
            TokKind.In or
            TokKind.Exactin or
            TokKind.Self or
            TokKind.Parent or
            TokKind.KeyOr or
            TokKind.KeyAnd or
            TokKind.KeyNot or
            TokKind.As or
            TokKind.IslandStart or
            TokKind.IslandEnd or
            TokKind.DoubleBarrelArrow or
            TokKind.BracketClose => SemanticTokenType.Keyword,

            _ => SemanticTokenType.Default,
        };
    }
}
