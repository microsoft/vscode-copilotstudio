namespace Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Validation
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.Agents.ObjectModel.Schema;
    using Microsoft.Agents.ObjectModel.Syntax;
    using Microsoft.Agents.ObjectModel.Syntax.Tokens;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Validation;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Models;
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using Range = Microsoft.PowerPlatformLS.Contracts.Lsp.Models.Range;

    internal sealed class InlineCommentValidationRule : IValidationRule<McsLspDocument>
    {
        internal const string DiagnosticCode = "InlineCommentInText";
        private const string MetadataProperty = "mcs.metadata";
        private const string MetadataComponentName = "componentName";
        private const string MetadataDescription = "description";
        private const string PowerFxExpressionPrefix = "=";

        private static readonly HashSet<BotElementKind> FreeFormTextElementKinds = new()
        {
            BotElementKind.StringExpression,
            BotElementKind.ActivityTemplate,
            BotElementKind.MessageActivityTemplate,
            BotElementKind.EventActivityTemplate,
        };

        IEnumerable<Diagnostic> IValidationRule<McsLspDocument>.ComputeValidation(RequestContext context, McsLspDocument document)
        {
            if (document.FileModel?.Syntax == null || document.Text.IndexOf('#') < 0)
            {
                yield break;
            }

            SyntaxToken? unquotedValueToken = null;
            foreach (var token in document.FileModel.Syntax.EnumerateTokens())
            {
                if (token.IsLineBreak)
                {
                    unquotedValueToken = null;
                }
                else if (token.Kind == SyntaxTokenKind.UnquotedValue)
                {
                    unquotedValueToken = token;
                }
                else if (token.Kind == SyntaxTokenKind.Comment && unquotedValueToken != null && TryCreateDiagnostic(document, unquotedValueToken, token, out var diagnostic))
                {
                    yield return diagnostic;
                    unquotedValueToken = null;
                }
            }
        }

        private static bool TryCreateDiagnostic(McsLspDocument document, SyntaxToken valueToken, SyntaxToken commentToken, out Diagnostic diagnostic)
        {
            diagnostic = default!;
            if (!IsSupportedStringValue(valueToken))
            {
                return false;
            }

            var range = document.MarkResolver.GetRange(valueToken.Position, commentToken.EndPosition);
            diagnostic = new Diagnostic
            {
                Code = DiagnosticCode,
                Range = range,
                Severity = DiagnosticSeverity.Warning,
                Message = "Inline # starts a YAML comment. Quote this value if the text after # is part of the value.",
                Data = new DiagnosticData
                {
                    Quickfix = [CreateQuoteValueCodeAction(document, valueToken, commentToken, range)],
                },
            };
            return true;
        }

        private static CodeAction CreateQuoteValueCodeAction(McsLspDocument document, SyntaxToken valueToken, SyntaxToken commentToken, Range range)
        {
            return new CodeAction
            {
                Title = "Quote YAML value",
                Kind = CodeActionKind.QuickFix,
                Edit = new WorkspaceEdit
                {
                    DocumentChanges = [new TextDocumentEdit
                    {
                        TextDocument = new VersionedTextDocumentIdentifier { Uri = document.Uri },
                        Edits = [new TextEdit { Range = range, NewText = QuoteYamlScalar(document.Text.Substring(valueToken.Position, commentToken.EndPosition - valueToken.Position).TrimEnd()) }],
                    }],
                },
            };
        }

        private static bool IsSupportedStringValue(SyntaxToken valueToken)
        {
            if (TryGetMetadataProperty(valueToken, out var metadataPropertyName))
            {
                return IsSupportedMetadataProperty(metadataPropertyName);
            }

            if (IsPowerFxExpressionValue(valueToken))
            {
                return false;
            }

            return IsSupportedSchemaProperty(valueToken, isSequenceItem: false) || IsSupportedSchemaProperty(valueToken, isSequenceItem: true);
        }

        private static bool IsPowerFxExpressionValue(SyntaxToken valueToken)
        {
            if (valueToken.RawText == null)
            {
                return false;
            }

            return valueToken.RawText.TrimStart().StartsWith(PowerFxExpressionPrefix, StringComparison.Ordinal);
        }

        private static bool IsSupportedSchemaProperty(SyntaxToken valueToken, bool isSequenceItem)
        {
            var isResolved = isSequenceItem ? TryGetSequenceItemProperty(valueToken, out var element, out var propertyName) : TryGetMappingProperty(valueToken, out element, out propertyName);

            if (!isResolved || !TryGetPropertyInfo(element, propertyName, out var schemaProperty))
            {
                return false;
            }

            return IsFreeFormStringProperty(schemaProperty, element, propertyName, isSequenceItem);
        }

        private static bool TryGetMappingProperty(SyntaxToken valueToken, out BotElement element, out string propertyName)
        {
            element = default!;
            propertyName = string.Empty;
            if (valueToken.Parent is IMappingKeyValueSyntax keyValueSyntax && keyValueSyntax.Value == valueToken && keyValueSyntax.PropertyName.Value is string name && keyValueSyntax.Parent is MappingObjectSyntax mappingObject)
            {
                element = mappingObject.GetElement();
                propertyName = name;
                return element != null;
            }

            return false;
        }

        private static bool TryGetSequenceItemProperty(SyntaxToken valueToken, out BotElement element, out string propertyName)
        {
            element = default!;
            propertyName = string.Empty;
            if (valueToken.Parent is SequenceElementSyntax sequenceElement && sequenceElement.Parent is MappingSequenceSyntax mappingSequence && mappingSequence.Parent is IMappingKeyValueSyntax keyValueSyntax && keyValueSyntax.PropertyName.Value is string name && keyValueSyntax.Parent is MappingObjectSyntax mappingObject)
            {
                element = mappingObject.GetElement();
                propertyName = name;
                return element != null;
            }

            return false;
        }

        private static bool TryGetMetadataProperty(SyntaxToken valueToken, out string metadataPropertyName)
        {
            metadataPropertyName = string.Empty;
            if (valueToken.Parent is IMappingKeyValueSyntax metadataValueSyntax && metadataValueSyntax.Value == valueToken && metadataValueSyntax.PropertyName.Value is string propertyName && metadataValueSyntax.Parent is MappingObjectSyntax metadataObject && metadataObject.Parent is IMappingKeyValueSyntax metadataObjectSyntax && string.Equals(metadataObjectSyntax.PropertyName.Value, MetadataProperty, StringComparison.Ordinal))
            {
                metadataPropertyName = propertyName;
                return true;
            }

            return false;
        }

        private static bool IsFreeFormStringProperty(SchemaPropertyInfo schemaProperty, BotElement element, string propertyName, bool isSequenceItem)
        {
            if (schemaProperty.IsCollection != isSequenceItem)
            {
                return false;
            }

            if (schemaProperty.ElementKind == ElementType.PrimitiveOrEnum)
            {
                return IsFreeFormTextPrimitive(schemaProperty.PrimitiveKind);
            }

            if (schemaProperty.ElementKind != ElementType.BotElement)
            {
                return false;
            }

            return AllKindsAreFreeFormText(schemaProperty.Kinds) || (!isSequenceItem && IsStringLiteralValueExpression(element, propertyName));
        }

        private static bool AllKindsAreFreeFormText(ImmutableArray<BotElementKind> kinds)
        {
            if (kinds.IsDefaultOrEmpty)
            {
                return false;
            }

            foreach (var kind in kinds)
            {
                if (!FreeFormTextElementKinds.Contains(kind))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool TryGetPropertyInfo(BotElement element, string propertyName, out SchemaPropertyInfo schemaProperty) => SchemaData.Properties.TryGetValue((element.Kind, propertyName), out schemaProperty!);

        private static bool IsFreeFormTextPrimitive(PrimitiveKind primitiveKind) => (primitiveKind == PrimitiveKind.@string || primitiveKind == PrimitiveKind.TemplateLine) && !SchemaData.EnumValues.ContainsKey(primitiveKind);

        private static bool IsStringLiteralValueExpression(BotElement element, string propertyName) => BotElementReflection.GetPropertyValueOrNull(element, propertyName) is ValueExpression valueExpression && valueExpression.IsLiteral && valueExpression.LiteralValue is StringDataValue;

        private static bool IsSupportedMetadataProperty(string propertyName) => string.Equals(propertyName, MetadataComponentName, StringComparison.Ordinal) || string.Equals(propertyName, MetadataDescription, StringComparison.Ordinal);

        private static string QuoteYamlScalar(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}
