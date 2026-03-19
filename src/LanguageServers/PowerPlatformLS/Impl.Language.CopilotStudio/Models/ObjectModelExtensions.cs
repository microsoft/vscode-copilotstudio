namespace Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Models
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.Agents.ObjectModel.Syntax;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Utilities;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Completion;
    using System.Text.Json;

    internal static class ObjectModelExtensions
    {
        /// <summary>
        /// Given an element parsed from a file, find the matching element in the list of descendant of a BotDefinition.
        /// </summary>
        internal static BotElement FindElementBySyntaxUri(this BotElement element, Uri syntaxUri)
        {
            foreach (var node in element.DescendantsAndSelf(descendIntoChildren: static e => e.Syntax is null or FileSyntax))
            {
                if (node.Syntax != null && node.Syntax.SourceUri == syntaxUri)
                {
                    return node;
                }
            }

            throw new InvalidOperationException($"No descendant found for {syntaxUri}.");
        }

        internal static IEnumerable<Contracts.Lsp.Models.Diagnostic> ToLspDiagnostics(this BotElementDiagnostic diagnostic, BotElement parent, MarkResolver markResolver)
        {
            var kindNumber = (int)diagnostic.Kind;

            DiagnosticSeverity sev = kindNumber > (int)BotElementDiagnosticKind.ElementValidationWarning ?
                DiagnosticSeverity.Error :
                kindNumber > (int)BotElementDiagnosticKind.VariableInformationDiagnostic ?
                    DiagnosticSeverity.Warning :
                    DiagnosticSeverity.Information;
            var diagnosticMessage = diagnostic.ErrorMessage;

            IEnumerable<Range> highlightRanges = diagnostic.GetTargetRanges(parent, markResolver);

            var errorCode = diagnostic.GetErrorCode();

            foreach (var errorRange in highlightRanges)
            {
                var diagnosticData = new DiagnosticData
                {
                    Quickfix = CodeActionHelper.GetSuggestions(diagnostic, errorRange, parent),
                };
                var ambibuityMessage = CreateAmbiguityMessage(errorRange, highlightRanges);
                yield return new Diagnostic
                {
                    Code = errorCode,
                    Range = errorRange,
                    Severity = sev,
                    Message = diagnosticMessage + ambibuityMessage,
                    Data = diagnosticData,
                };
            }
        }

        private static IEnumerable<Range> GetTargetRanges(this BotElementDiagnostic diagnostic, BotElement parent, MarkResolver markResolver)
        {
            IEnumerable<Range> ranges;
            switch (diagnostic)
            {
                case PropertyErrorBase propErrorBase:
                    var propSyntax = parent.GetPropertyNodeByName(propErrorBase.PropertyName);
                    ranges = [(propSyntax ?? parent.GetFirstParentSyntax()).GetRange(markResolver)];
                    if (propErrorBase is DuplicatePropertyError propError &&
                        propError.ErrorCode?.Value == ValidationErrorCode.DuplicateActionId)
                    {
                        var dupElements = parent.FindPropertyNodesForDuplicateId(markResolver);
                        var dupRanges = dupElements.Select(x => x.GetRange(markResolver));
                        ranges = ranges.Concat(dupRanges);
                    }
                    return ranges;
                case InvalidReferenceError refError:
                    ranges = parent.GetPropertyNodesByValue(refError.ReferenceId)
                        .Select(x => x.GetRange(markResolver));
                    if (!ranges.Any())
                    {
                        ranges = [parent.Syntax.GetRange(markResolver)];
                    }
                    return ranges;
                case ExpressionError expressionError:
                    return [GetRangeForExpressionError(expressionError, parent, markResolver)];
                default:
                    return [parent.Syntax.GetRange(markResolver)];
            }
        }

        private static Range GetRangeForExpressionError(ExpressionError expressionError, BotElement parent, MarkResolver markResolver)
        {
            var parentSyntax = parent.Syntax;
            if (parentSyntax != null)
            {
                var initialOffset = parentSyntax.Position;
                if (parentSyntax is SyntaxToken token && token.RawText != null && token.RawText.StartsWith("="))
                {
                    initialOffset++;
                }

                return markResolver.GetRange(initialOffset + expressionError.StartPosition, initialOffset + expressionError.EndPosition);
            }
            else if (parent.ParentOfType<TemplateLine>() is TemplateLine templateLine && parent.Parent is ExpressionSegment exprSegment)
            {
                foreach (var (segment, offset) in templateLine.GetSegmentsWithSpans())
                {
                    if (ReferenceEquals(segment, exprSegment))
                    {
                        var initialOffset = offset.Start + 1; // skip past '{'
                        var startOffset = initialOffset + expressionError.StartPosition;
                        var endOffset = expressionError.EndPosition + offset.Start + 1; // skip past '{'
                        return markResolver.GetRange(startOffset, endOffset);
                    }
                }
            }

            return parentSyntax.GetRange(markResolver);
        }

        private static SyntaxNode? GetFirstParentSyntax(this BotElement element)
        {
            while (element.Syntax == null && element.Parent != null)
            {
                element = element.Parent;
            }

            return element.Syntax;
        }

        private static IEnumerable<SyntaxNode?> FindPropertyNodesForDuplicateId(this BotElement target, MarkResolver markResolver)
        {
            var root = target;
            while (root.Parent != null)
            {
                root = root.Parent;
            }

            var elementsWithDuplicateId = root.Descendants()
                .Where(element => element != target && (element is DialogAction dialog && dialog.Id == (target as DialogAction)?.Id))
                .Select(element => element.GetPropertyNodeByName("Id"));
            return elementsWithDuplicateId;
        }

        private static Range GetRange(this SyntaxNode? node, MarkResolver markResolver, int? startOffset = null, int? endOffset = null)
        {
            if (node == null)
            {
                return Range.Zero;
            }

            var start = node.Position + (startOffset ?? 0);
            int end = endOffset != null ? (node.Position + (int)endOffset) : node.EndPosition;
            var range = markResolver.GetRange(start, end);
            return range;
        }

        private static IEnumerable<SyntaxNode?> GetPropertyNodesByValue(this BotElement element, string? propertyValue)
        {
            var syntax = element.Syntax as MappingObjectSyntax;
            foreach (var children in syntax?.AllProperties() ?? [])
            {
                if (children.Value is SyntaxToken valueToken && valueToken.RawText?.Equals(propertyValue, StringComparison.OrdinalIgnoreCase) == true)
                {
                    yield return children.Value;
                }
            }
        }

        private static SyntaxNode? GetPropertyNodeByName(this BotElement element, string? propertyName)
        {
            var syntax = element.Syntax as MappingObjectSyntax;
            if (syntax == null)
            {
                return null;
            }

            foreach (var children in syntax.AllProperties())
            {
                if (children.PropertyName.Value?.Equals(propertyName, StringComparison.OrdinalIgnoreCase) == true)
                {
                    return children.Value;
                }
            }

            return null;
        }

        private static string CreateAmbiguityMessage(Range errorRange, IEnumerable<Range> ambiguousRanges)
        {
            if (ambiguousRanges.Count() <= 1)
            {
                return string.Empty;
            }

            string otherLines = string.Join(", ", ambiguousRanges.Where(x => x.Start.Line != errorRange.Start.Line).Select(r => (r.Start.Line + 1).ToString()));
            string pluralSuffix = otherLines.Contains(',') ? "s" : string.Empty;
            var ambiguityMessage = $"\nAmbiguity (duplicate id) found on line{pluralSuffix}: {otherLines}";
            return ambiguityMessage;
        }

        private static string GetErrorCode(this BotElementDiagnostic diagnostic)
        {
            // return the value of property "ErrorCode" if it exists
            var errorCodeProperty = diagnostic.GetType().GetProperty("ErrorCode");
            return errorCodeProperty?.GetValue(diagnostic)?.ToString() ?? diagnostic.Kind.ToString();
        }
    }
}
