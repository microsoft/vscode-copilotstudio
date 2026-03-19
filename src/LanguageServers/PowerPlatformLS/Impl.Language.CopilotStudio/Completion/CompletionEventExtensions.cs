namespace Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Completion
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.Agents.ObjectModel.Syntax;
    using Microsoft.Agents.ObjectModel.Syntax.Text;
    using Microsoft.Agents.ObjectModel.Syntax.Tokens;
    using Microsoft.PowerFx;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Models;
    using System.Diagnostics.CodeAnalysis;

    internal static class CompletionEventExtensions
    {
        /// <summary>
        /// Determine what kind of intellisense completion event just occured.
        /// This is heavy pattern matching against the syntax tree and trigger information.
        /// Return null if there is no determination.
        /// </summary>
        public static CompletionEvent? Triage(this RequestContext requestContext, CompletionContext triggerContext)
        {
            var doc = requestContext.Document.As<McsLspDocument>();
            var workspace = (McsWorkspace)requestContext.Workspace;
            var indentLevelAtCursor = GetIndentLevel(doc, requestContext.Index, out bool isEmptyLine);
            if (!TryGetSyntax(workspace, doc, requestContext.Index,
                out var syntaxTokenAtCursor,
                out var lastNonTriviaTokenAtCursor))
            {
                return null;
            }

            var elementAtCursor = GetElementAtCursor(syntaxTokenAtCursor, requestContext.Index);

            if (requestContext.Document.Text.Length == 0)
            {
                if (elementAtCursor != null)
                {
                    return new NewPropertyCompletionEvent
                    {
                        Element = elementAtCursor,
                        Parent = null,
                        Syntax = syntaxTokenAtCursor,
                        CurrentToken = syntaxTokenAtCursor
                    };
                }
            }
            else if (TryGetErrorSyntax(syntaxTokenAtCursor, out var errorSyntax))
            {
                return new NewPropertyCompletionEvent
                {
                    Element = elementAtCursor,
                    Parent = errorSyntax.ParentOfType<MappingObjectSyntax>(),
                    Syntax = errorSyntax,
                    CurrentToken = syntaxTokenAtCursor,
                };
            }
            else if (IsEditingPropertyNameOrValue(syntaxTokenAtCursor, out var kvp, out var propertyValueSyntax))
            {
                if (propertyValueSyntax != null)
                {
                    // example:
                    // key: value

                    return new EditPropertyValueCompletionEvent
                    {
                        Parent = ((MappingObjectSyntax?)kvp.Parent) ?? throw new InvalidOperationException("Need parent on kvp"),
                        PropertyName = kvp.PropertyName.Value ?? throw new InvalidOperationException("Missing property value for edit"),
                        Element = elementAtCursor,
                        CurrentToken = syntaxTokenAtCursor
                    };
                }
                else
                {
                    // key without value
                    return new NewPropertyCompletionEvent
                    {
                        Element = elementAtCursor,
                        Parent = null,
                        Syntax = syntaxTokenAtCursor.Parent,
                        CurrentToken = syntaxTokenAtCursor,
                    };
                }
            }
            else if (syntaxTokenAtCursor.Parent is SequenceElementSyntax seq
                && seq.Parent is MappingSequenceSyntax seq2
                && syntaxTokenAtCursor?.Kind == SyntaxTokenKind.UnquotedValue)
            {
                int i = 0;

                // Which sequence element are we in.
                foreach (var child in seq2.Elements.EnumerateChildren(false, _ => false))
                {
                    if (child.FullSpan.Contains(requestContext.Index))
                    {
                        return new SequenceValueCompletionEvent
                        {
                            Element = elementAtCursor,
                            Index = i,
                            CurrentToken = syntaxTokenAtCursor,

                        };
                    }
                    i++;
                }
            }
            else if (TryResolveParentObject(syntaxTokenAtCursor, lastNonTriviaTokenAtCursor, indentLevelAtCursor, out var objectSyntax, out var lastKeyValuePair))
            {
                if (objectSyntax.GetIndentLevel() == indentLevelAtCursor)
                {
                    // Editing an existing property
                    if (lastKeyValuePair?.PropertyName.Value is string lastPropertyName
                        && lastNonTriviaTokenAtCursor.Kind == SyntaxTokenKind.PropertyName
                        && lastPropertyName == lastNonTriviaTokenAtCursor.Value
                        && !isEmptyLine)
                    {
                        var value = BotElementReflection.GetPropertyValueOrNull(objectSyntax.GetElement(), lastPropertyName);
                        return new EditPropertyValueCompletionEvent
                        {
                            Parent = objectSyntax,
                            Element = objectSyntax.GetElement(),
                            PropertyName = lastPropertyName,
                            CurrentToken = syntaxTokenAtCursor,
                        };
                    }

                    return new NewPropertyCompletionEvent
                    {
                        Element = objectSyntax.GetElement(),
                        Parent = objectSyntax,
                        Syntax = objectSyntax,
                        CurrentToken = syntaxTokenAtCursor,
                    };
                }
                else if (objectSyntax.GetIndentLevel() < indentLevelAtCursor && lastKeyValuePair?.PropertyName.Value is string lastPropertyName) // the indentation has increased. Edit the last property
                {
                    var value = BotElementReflection.GetPropertyValueOrNull(objectSyntax.GetElement(), lastPropertyName);
                    return new EditPropertyValueCompletionEvent
                    {
                        Parent = objectSyntax,
                        Element = value as BotElement,
                        PropertyName = lastPropertyName,
                        CurrentToken = syntaxTokenAtCursor,
                    };
                }
            }
            else if (syntaxTokenAtCursor?.IsTrivia == true
                && TryFindParentObject(lastNonTriviaTokenAtCursor, out var parentKvp)
                && parentKvp.Parent is MappingObjectSyntax parentObject)
            {
                if (parentKvp.Separator.FullWidth > 0
                    && parentKvp.PropertyName.Value != null
                    && requestContext.Index <= parentKvp.LineBreak.Position)
                {
                    return new EditPropertyValueCompletionEvent
                    {
                        Element = parentObject.GetElement(),
                        Parent = parentObject,
                        PropertyName = parentKvp.PropertyName.Value,
                        CurrentToken = syntaxTokenAtCursor,
                    };
                }
                else
                {
                    return new NewPropertyCompletionEvent
                    {
                        Element = parentObject.GetElement(),
                        Parent = parentObject,
                        Syntax = parentObject,
                        CurrentToken = syntaxTokenAtCursor,
                    };
                }
            }

            return null;
        }

        private static int GetIndentLevel(McsLspDocument doc, int index, out bool isEmptyLine)
        {
            var position = doc.MarkResolver.GetPosition(index);
            var column = position.Character;
            var span = doc.Text.AsSpan();
            var offset = index - position.Character;
            var size = Math.Min(span.Length - offset, position.Character + 1);
            var range = span.Slice(offset, size);
            var indexOfNonSpace = range.IndexOfAnyExcept(' ');
            if (indexOfNonSpace < 0)
            {
                isEmptyLine = true;
                return column;
            }
            else if(size == span.Length - offset || char.IsWhiteSpace(range[range.Length - 1]))
            {
                isEmptyLine = false;
                // Adding a new property
                return indexOfNonSpace;
            }
            else
            {
                isEmptyLine = false;
                // mutating an existing thing
                return -1;
            }
        }

        private static BotElement GetElementAtCursor(SyntaxToken syntaxTokenAtCursor, int index)
        {
            var element = syntaxTokenAtCursor.GetElement();
            if (element is TemplateLine tl)
            {
                TemplateSegment? previousSegment = null;
                foreach (var item in tl.GetSegmentsWithSpans())
                {
                    if (item.rawOffsetSpan.Contains(index))
                    {
                        if (item.segment is ExpressionSegment expr)
                        {
                            // text {expression}
                            //      ^ cursor 
                            // if we are at the bounds of the expression segment, we want to snap to the text
                            // the expression is surrounded by curly braces.
                            if (item.rawOffsetSpan.Start == index)
                            {
                                return previousSegment ?? element;
                            }
                            else
                            {
                                return expr.Expression ?? element;
                            }
                        }
                        else
                        {
                            return item.segment;
                        }
                    }

                    previousSegment = item.segment;
                }
            }

            return element;
        }

        private static bool IsEditingPropertyNameOrValue(SyntaxToken syntaxTokenAtCursor, [NotNullWhen(true)] out IMappingKeyValueSyntax? kvp, out SyntaxNode? propertyValueSyntax)
        {
            //syntaxTokenAtCursor.Kind is SyntaxTokenKind.PropertyName
            //    && syntaxTokenAtCursor.Parent is IMappingKeyValueSyntax kvp
            //    && kvp.Value.FullWidth == 0 && kvp.Separator.FullWidth == 0
            kvp = null;
            propertyValueSyntax = null;

            if (syntaxTokenAtCursor.Kind is SyntaxTokenKind.PropertyName && syntaxTokenAtCursor.Parent is IMappingKeyValueSyntax kvp2)
            {
                kvp = kvp2;
            }
            else
            {
                propertyValueSyntax = syntaxTokenAtCursor.Parent is MultilineString ? syntaxTokenAtCursor.Parent : syntaxTokenAtCursor;
                if (propertyValueSyntax.Parent is IMappingKeyValueSyntax kvp3 && kvp3.Value == propertyValueSyntax)
                {
                    kvp = kvp3;
                    return true;
                }
            }

            return kvp != null;
        }

        private static bool TryResolveParentObject([NotNullWhen(true)] SyntaxToken? syntaxTokenAtCursor, [NotNullWhen(true)] SyntaxToken? lastNonTriviaTokenAtCursor, int indentLevelAtCursor,
            [NotNullWhen(true)] out MappingObjectSyntax? objectSyntax,
            out IMappingKeyValueSyntax? lastKeyValuePair)
        {
            lastKeyValuePair = null;
            if (indentLevelAtCursor < 0)
            {
                return TryResolveParentObject(syntaxTokenAtCursor, out objectSyntax, out lastKeyValuePair);
            }
            else if (lastNonTriviaTokenAtCursor is not null)
            {
                foreach (var parent in lastNonTriviaTokenAtCursor.Parents())
                {
                    lastKeyValuePair = parent as IMappingKeyValueSyntax ?? lastKeyValuePair;
                    if (parent is IIndentedSyntax candidateObject)
                    {
                        var parentIndentLevel = candidateObject.GetIndentLevel();
                        if (parentIndentLevel <= indentLevelAtCursor)
                        {
                            objectSyntax = candidateObject as MappingObjectSyntax;
                            return objectSyntax != null;
                        }
                    }
                }
            }

            objectSyntax = null;
            return false;
        }

        private static bool TryResolveParentObject(SyntaxToken? syntaxTokenAtCursor, [NotNullWhen(true)] out MappingObjectSyntax? objectSyntax, out IMappingKeyValueSyntax? lastKeyValuePair)
        {
            if (syntaxTokenAtCursor?.Parent is MappingObjectSyntax obj)
            {
                lastKeyValuePair = null;
                objectSyntax = obj;
            }
            else if (syntaxTokenAtCursor?.Parent is IMappingKeyValueSyntax parentKvp)
            {
                lastKeyValuePair = parentKvp;
                objectSyntax = parentKvp.Parent as MappingObjectSyntax;
            }
            else if (syntaxTokenAtCursor?.Parent is SyntaxTrivia trivia && trivia.Parent is IMappingKeyValueSyntax kvp)
            {
                lastKeyValuePair = kvp;
                objectSyntax = kvp.Parent as MappingObjectSyntax;
            }
            else
            {
                lastKeyValuePair = null;
                objectSyntax = null;
            }

            return objectSyntax != null;
        }

        private static bool TryGetErrorSyntax(SyntaxToken syntaxTokenAtCursor, [NotNullWhen(true)] out ErrorSyntax? error)
        {
            if (syntaxTokenAtCursor.Parent is ErrorSyntax err)
            {
                error = err;
                return true;
            }
            else
            {
                error = null;
                return false;
            }
        }

        private static bool TryGetSyntax(
            McsWorkspace workspace,
            McsLspDocument doc,
            int index,
            [NotNullWhen(true)] out SyntaxToken? syntaxTokenAtCursor,
            [NotNullWhen(true)] out SyntaxToken? lastNonTriviaTokenAtCursor)
        {
            // ! caller has built the model.
            var analyzer = workspace.CompilationAnalyzer!;
            var rootElement = analyzer.GetDocumentRoot(doc);

            var syntax = rootElement.Syntax;

            syntaxTokenAtCursor = (SyntaxToken?)syntax?.GetSyntaxNodeAtPosition(index);
            if (syntaxTokenAtCursor == null)
            {
                lastNonTriviaTokenAtCursor = null;
                return false;
            }

            if (syntaxTokenAtCursor.IsTrivia)
            {
                if (index > 0 && syntaxTokenAtCursor.Position == index)
                {
                    // we are at the edge of a trivia token. For example:
                    // key: value\n
                    // cursor at ^, just before the new line. Bind to "value" instead of the newline.
                    var previousToken = (SyntaxToken)syntax.GetSyntaxNodeAtPosition(index - 1);
                    if (!previousToken.IsTrivia)
                    {
                        // only apply the adjustment if the cursor is exactly in between the two tokens.
                        syntaxTokenAtCursor = previousToken;
                    }
                }
                else if (index < doc.Text.Length - 1 && index == syntaxTokenAtCursor.EndPosition)
                {
                    // we are at the edge of a trivia token. For example:
                    // key: value\n
                    //      ^ cursor before "v". Bind to "value" instead of the newline sequence separator token
                    var nextToken = (SyntaxToken)syntax.GetSyntaxNodeAtPosition(index + 1);
                    if (!nextToken.IsTrivia)
                    {
                        // only apply the adjustment if the cursor is exactly in between the two tokens.
                        syntaxTokenAtCursor = nextToken;
                    }
                }

                lastNonTriviaTokenAtCursor = syntaxTokenAtCursor;
                while (index > 0 && lastNonTriviaTokenAtCursor.IsTrivia)
                {
                    index -= lastNonTriviaTokenAtCursor.FullWidth;
                    if (index > 0)
                    {
                        lastNonTriviaTokenAtCursor = (SyntaxToken)syntax.GetSyntaxNodeAtPosition(index);
                    }
                }
            }
            else
            {
                lastNonTriviaTokenAtCursor = syntaxTokenAtCursor;
            }

            return true;
        }

        private static bool TryFindParentObject(SyntaxNode node, [NotNullWhen(true)] out IMappingKeyValueSyntax? mappingKeyValueSyntax)
        {
            if (node.Parent is SequenceElementSyntax sequenceElementSyntax)
            {
                mappingKeyValueSyntax = sequenceElementSyntax.GetValue() as IMappingKeyValueSyntax;
                return mappingKeyValueSyntax != null;
            }
            else if (node.Parent is IMappingKeyValueSyntax kvp)
            {
                mappingKeyValueSyntax = kvp;
                return true;
            }
            else
            {
                mappingKeyValueSyntax = null;
                return false;
            }
        }

        // Get Power Fx CheckResult from a ExpressionBase. 
        public static bool TryGetCheckResult(
            this ExpressionBase expr,
            RequestContext requestContext,
            [NotNullWhen(returnValue: true)] out CheckResult? check,
            [NotNullWhen(returnValue: true)] out Engine? engine)
        {
            check = null;
            engine = null;

            if (!expr.IsExpression)
            {
                return false;
            }

            var expressionContext = new ExpressionContext
            {
                ExpressionText = expr.ExpressionText,
                Element = expr,
                Workspace = (McsWorkspace)requestContext.Workspace,
                Offset = 0
            };

            return expressionContext.TryGetCheckResult(null, out check, out engine);
        }

        // Helper to get Segments with spans.
        public static IEnumerable<(TemplateSegment segment, TextSpan rawOffsetSpan)> GetSegmentsWithSpans(this TemplateLine templateLine)
        {
            var position = templateLine.Syntax?.Position ?? throw new InvalidOperationException("Need syntax");
            var segmentEnumerator = templateLine.GetSegmentStartOffsetsAndLengths().GetEnumerator();
            foreach (var segment in templateLine.Segments)
            {
                if (!segmentEnumerator.MoveNext())
                {
                    break;
                }

                var x = segmentEnumerator.Current;
                var span = new TextSpan(x.startOffset + position, x.length);

                yield return (segment, span);
            }
        }

        // Convert a document position (based on SyntaxNodes) into an offset for the given expression.
        public static bool TryConvertPositionToExpressionOffset(this ExpressionBase expr, int position, out int offset)
        {
            if (expr.IsExpression &&
                expr.Syntax != null &&
                expr.Syntax is SyntaxToken exprToken)
            {
                // For expressions, leading character is a '='.
                // This should be true since IsExpression is true, but confirm...
                if (exprToken.Value?.StartsWith("=") == true)
                {
                    if (exprToken.TryMapFileOffsetToValueOffset(position, out offset))
                    {
                        if (offset <= 0)
                        {
                            // This is a bug in our code.
                            // Leading '=' means offset >= 1. 
                            throw new InvalidOperationException($"bad offset"); 
                        }

                        offset--; // skip past leading '='
                        return true;
                    }
                }
            }
            else if (expr.Parent is ExpressionSegment seg && seg.Parent is TemplateLine tl)
            {
                foreach (var item in tl.GetSegmentsWithSpans())
                {
                    if (ReferenceEquals(item.segment, seg))
                    {
                        offset = position - item.rawOffsetSpan.Start - 1; // skip past '{';
                        return true;
                    }
                }
            }

            offset = -1;
            return false;
        }
    }
}