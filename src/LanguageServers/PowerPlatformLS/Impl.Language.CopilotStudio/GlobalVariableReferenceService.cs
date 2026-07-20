namespace Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.Agents.ObjectModel.Syntax;
    using Microsoft.PowerFx;
    using Microsoft.PowerFx.Syntax;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Utilities;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Completion;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Models;
    using System.Collections.Generic;
    using System.Linq;

    internal readonly record struct GlobalVariableIdentity(string VariableName);

    internal enum GlobalVariableReferenceKind
    {
        Definition,

        DefinitionComponentName,

        SetVariableTarget,

        ExpressionUsage,
    }

    internal sealed record GlobalVariableReference(System.Uri SourceUri, Range Range, GlobalVariableReferenceKind Kind);

    internal interface IGlobalVariableReferenceService
    {
        bool TryResolveIdentityAtPosition(RequestContext context, out GlobalVariableIdentity identity);

        IReadOnlyList<GlobalVariableReference> FindReferences(RequestContext context, GlobalVariableIdentity identity);

        IReadOnlyList<string> GetGlobalVariableNames(RequestContext context);

        bool TryGetDefinitionUri(RequestContext context, GlobalVariableIdentity identity, out System.Uri definitionUri);
    }

    internal sealed class GlobalVariableReferenceService : IGlobalVariableReferenceService
    {
        internal const string GlobalNamespacePrefix = "Global.";
        internal const string GlobalVariableInfix = ".globalvariable.";
        internal const string VariablesFolder = "variables/";
        private const string NamePropertyName = "name";
        private const string MetadataPropertyName = "mcs.metadata";
        private const string ComponentNamePropertyName = "componentName";

        public bool TryResolveIdentityAtPosition(RequestContext context, out GlobalVariableIdentity identity)
        {
            identity = default;
            var element = context.GetCurrentElement();
            if (element == null)
            {
                return false;
            }

            var variableName = ResolveVariableNameAtCursor(context, element);
            if (string.IsNullOrEmpty(variableName))
            {
                return false;
            }

            if (element.ParentOfType<BotDefinition>()?.Entity?.SchemaName == null)
            {
                return false;
            }

            identity = new GlobalVariableIdentity(variableName!);
            return true;
        }

        public IReadOnlyList<GlobalVariableReference> FindReferences(RequestContext context, GlobalVariableIdentity identity)
        {
            var workspace = (McsWorkspace)context.Workspace;
            var definition = workspace.Definition;
            var results = new List<GlobalVariableReference>();
            Engine? engine = null;

            foreach (var node in definition.DescendantsAndSelf())
            {
                switch (node)
                {
                    case GlobalVariableComponent globalVariable when VariableNameMatches(globalVariable, identity.VariableName):
                        AddDefinitionReferences(globalVariable, workspace, results);
                        break;
                    case SetVariable setVariable when IsGlobalReference(setVariable.Variable?.Path, identity.VariableName):
                        AddSetVariableTargetReference(setVariable, workspace, identity.VariableName, results);
                        break;
                    case ExpressionBase expression:
                        AddExpressionReferences(expression, context, workspace, identity.VariableName, ref engine, results);
                        break;
                    case TemplateLine templateLine:
                        AddTemplateLineReferences(templateLine, context, workspace, identity.VariableName, ref engine, results);
                        break;
                }
            }

            return results;
        }

        public IReadOnlyList<string> GetGlobalVariableNames(RequestContext context)
        {
            var workspace = (McsWorkspace)context.Workspace;
            return workspace.Definition.DescendantsAndSelf().OfType<GlobalVariableComponent>().Select(globalVariable => GetVariableName(globalVariable.SchemaNameString)).Where(name => !string.IsNullOrEmpty(name)).Select(name => name!).Distinct().ToList();
        }

        public bool TryGetDefinitionUri(RequestContext context, GlobalVariableIdentity identity, out System.Uri definitionUri)
        {
            definitionUri = null!;
            var workspace = (McsWorkspace)context.Workspace;
            var globalVariable = workspace.Definition.DescendantsAndSelf().OfType<GlobalVariableComponent>().FirstOrDefault(candidate => VariableNameMatches(candidate, identity.VariableName));
            var sourceUri = globalVariable?.Variable?.Syntax?.SourceUri;
            if (sourceUri == null)
            {
                return false;
            }

            definitionUri = sourceUri;
            return true;
        }

        private string? ResolveVariableNameAtCursor(RequestContext context, BotElement element)
        {
            switch (element)
            {
                case SetVariable setVariable when IsGlobalNamespace(setVariable.Variable?.Path) && IsCursorOnSetVariableTarget(setVariable, context.Index):
                    return setVariable.Variable!.Path.VariableName;
                case ExpressionBase expression when expression.IsVariableReference && IsGlobalNamespace(expression.VariableReference):
                    return expression.VariableReference!.VariableName;
                case ExpressionBase expression when expression.IsExpression:
                    return ResolveNameInExpressionAtCursor(context, expression);
                case TemplateLine templateLine:
                    return ResolveNameInTemplateAtCursor(context, templateLine);
            }

            var owningVariable = element as GlobalVariableComponent ?? element.ParentOfType<GlobalVariableComponent>();
            if (owningVariable?.Variable?.Syntax is MappingObjectSyntax definitionMapping && IsCursorOnDefinitionName(definitionMapping, context.Index))
            {
                return GetVariableName(owningVariable.SchemaNameString);
            }

            return null;
        }

        private static bool IsCursorOnDefinitionName(MappingObjectSyntax mapping, int index)
        {
            if (GetPropertyValue(mapping, NamePropertyName) is SyntaxNode nameValue && index >= nameValue.Position && index <= nameValue.EndPosition)
            {
                return true;
            }

            return GetPropertyValue(mapping, MetadataPropertyName) is MappingObjectSyntax metadata && GetPropertyValue(metadata, ComponentNamePropertyName) is SyntaxNode componentNameValue && index >= componentNameValue.Position && index <= componentNameValue.EndPosition;
        }

        private static bool IsCursorOnSetVariableTarget(SetVariable setVariable, int index)
        {
            return setVariable.Syntax is MappingObjectSyntax mapping && GetPropertyValue(mapping, "variable") is SyntaxNode valueNode && index >= valueNode.Position && index <= valueNode.EndPosition;
        }

        private static bool VariableNameMatches(GlobalVariableComponent globalVariable, string variableName) => string.Equals(GetVariableName(globalVariable.SchemaNameString), variableName, System.StringComparison.Ordinal);

        internal static string? GetVariableName(string? schemaName)
        {
            if (string.IsNullOrEmpty(schemaName))
            {
                return null;
            }

            var lastSeparator = schemaName!.LastIndexOf('.');
            return lastSeparator < 0 ? schemaName : schemaName.Substring(lastSeparator + 1);
        }

        private static bool IsGlobalNamespace(PropertyPath? path) => path != null && path.Namespace == VariableNamespace.Global;

        private static bool IsGlobalReference(PropertyPath? path, string variableName) => IsGlobalNamespace(path) && string.Equals(path!.VariableName, variableName, System.StringComparison.Ordinal);

        private void AddDefinitionReferences(GlobalVariableComponent globalVariable, McsWorkspace workspace, List<GlobalVariableReference> results)
        {
            if (globalVariable.Variable?.Syntax is not MappingObjectSyntax mapping)
            {
                return;
            }

            var sourceUri = mapping.SourceUri;
            var markResolver = GetMarkResolver(workspace, sourceUri);
            if (markResolver == null)
            {
                return;
            }

            var nameValue = GetPropertyValue(mapping, NamePropertyName);
            if (nameValue != null)
            {
                results.Add(new GlobalVariableReference(sourceUri, markResolver.GetRange(nameValue.Position, nameValue.EndPosition), GlobalVariableReferenceKind.Definition));
            }

            if (GetPropertyValue(mapping, MetadataPropertyName) is MappingObjectSyntax metadata && GetPropertyValue(metadata, ComponentNamePropertyName) is SyntaxNode componentName)
            {
                results.Add(new GlobalVariableReference(sourceUri, markResolver.GetRange(componentName.Position, componentName.EndPosition), GlobalVariableReferenceKind.DefinitionComponentName));
            }
        }

        private void AddSetVariableTargetReference(SetVariable setVariable, McsWorkspace workspace, string variableName, List<GlobalVariableReference> results)
        {
            if (setVariable.Syntax is not MappingObjectSyntax mapping || GetPropertyValue(mapping, "variable") is not SyntaxToken valueToken)
            {
                return;
            }

            var sourceUri = valueToken.SourceUri;
            var markResolver = GetMarkResolver(workspace, sourceUri);
            if (markResolver == null)
            {
                return;
            }

            if (TryGetNameSegmentRange(valueToken, variableName, markResolver, out var range))
            {
                results.Add(new GlobalVariableReference(sourceUri, range, GlobalVariableReferenceKind.SetVariableTarget));
            }
        }

        private void AddExpressionReferences(ExpressionBase expression, RequestContext context, McsWorkspace workspace, string variableName, ref Engine? engine, List<GlobalVariableReference> results)
        {
            if (expression.Syntax is not SyntaxToken valueToken)
            {
                return;
            }

            var sourceUri = valueToken.SourceUri;
            var markResolver = GetMarkResolver(workspace, sourceUri);
            if (markResolver == null)
            {
                return;
            }

            if (expression.IsVariableReference && IsGlobalReference(expression.VariableReference, variableName))
            {
                if (TryGetNameSegmentRange(valueToken, variableName, markResolver, out var range))
                {
                    results.Add(new GlobalVariableReference(sourceUri, range, GlobalVariableReferenceKind.ExpressionUsage));
                }

                return;
            }

            if (expression.IsExpression)
            {
                var expressionText = expression.ExpressionText;
                if (string.IsNullOrEmpty(expressionText))
                {
                    return;
                }

                var offsetMapper = valueToken.GetOffsetMapper();
                foreach (var range in FindGlobalUsagesInExpression(expression, expressionText, offsetMapper, context, variableName, ref engine, markResolver))
                {
                    results.Add(new GlobalVariableReference(sourceUri, range, GlobalVariableReferenceKind.ExpressionUsage));
                }
            }
        }

        private void AddTemplateLineReferences(TemplateLine templateLine, RequestContext context, McsWorkspace workspace, string variableName, ref Engine? engine, List<GlobalVariableReference> results)
        {
            if (templateLine.Syntax is not SyntaxToken valueToken)
            {
                return;
            }

            var sourceUri = valueToken.SourceUri;
            var markResolver = GetMarkResolver(workspace, sourceUri);
            if (markResolver == null)
            {
                return;
            }

            foreach (var (segment, span) in templateLine.GetSegmentsWithSpans())
            {
                if (segment is not ExpressionSegment expressionSegment || expressionSegment.Expression is not ExpressionBase expression)
                {
                    continue;
                }

                if (expression.IsVariableReference && IsGlobalReference(expression.VariableReference, variableName))
                {
                    var nameStart = span.Start + 1 + GlobalNamespacePrefix.Length;
                    results.Add(new GlobalVariableReference(sourceUri, markResolver.GetRange(nameStart, nameStart + variableName.Length), GlobalVariableReferenceKind.ExpressionUsage));
                }
                else if (expression.IsExpression)
                {
                    var expressionText = expression.ExpressionText;
                    if (string.IsNullOrEmpty(expressionText))
                    {
                        continue;
                    }

                    var offsetMapper = new UnquotedValueOffsetMapper(expressionText, span.Start);
                    foreach (var range in FindGlobalUsagesInExpression(expression, expressionText, offsetMapper, context, variableName, ref engine, markResolver))
                    {
                        results.Add(new GlobalVariableReference(sourceUri, range, GlobalVariableReferenceKind.ExpressionUsage));
                    }
                }
            }
        }

        private string? ResolveNameInExpressionAtCursor(RequestContext context, ExpressionBase expression)
        {
            if (expression.Syntax is not SyntaxToken valueToken)
            {
                return null;
            }

            var expressionText = expression.ExpressionText;
            if (string.IsNullOrEmpty(expressionText))
            {
                return null;
            }

            Engine? engine = null;
            var markResolver = GetMarkResolver((McsWorkspace)context.Workspace, valueToken.SourceUri);
            if (markResolver == null)
            {
                return null;
            }

            return FindGlobalNameAtCursor(expression, expressionText, valueToken.GetOffsetMapper(), context, markResolver, ref engine);
        }

        private string? ResolveNameInTemplateAtCursor(RequestContext context, TemplateLine templateLine)
        {
            if (templateLine.Syntax is not SyntaxToken valueToken)
            {
                return null;
            }

            var markResolver = GetMarkResolver((McsWorkspace)context.Workspace, valueToken.SourceUri);
            if (markResolver == null)
            {
                return null;
            }

            Engine? engine = null;
            foreach (var (segment, span) in templateLine.GetSegmentsWithSpans())
            {
                if (!span.Contains(context.Index) || segment is not ExpressionSegment expressionSegment || expressionSegment.Expression is not ExpressionBase expression)
                {
                    continue;
                }

                if (expression.IsVariableReference && IsGlobalNamespace(expression.VariableReference))
                {
                    return expression.VariableReference!.VariableName;
                }

                if (expression.IsExpression)
                {
                    var expressionText = expression.ExpressionText;
                    if (string.IsNullOrEmpty(expressionText))
                    {
                        return null;
                    }

                    return FindGlobalNameAtCursor(expression, expressionText, new UnquotedValueOffsetMapper(expressionText, span.Start), context, markResolver, ref engine);
                }
            }

            return null;
        }

        private static IEnumerable<Range> FindGlobalUsagesInExpression(ExpressionBase expression, string expressionText, OffsetMapper offsetMapper, RequestContext context, string variableName, ref Engine? engine, MarkResolver markResolver)
        {
            var ranges = new List<Range>();
            var tokens = TokenizeExpression(expression, context, ref engine, expressionText);
            if (tokens == null)
            {
                return ranges;
            }

            foreach (var nameToken in EnumerateGlobalNameTokens(tokens, expressionText, variableName))
            {
                if (offsetMapper.TryMapValueOffsetToFileOffset(nameToken.Span.Min, out var start) && offsetMapper.TryMapValueOffsetToFileOffset(nameToken.Span.Lim, out var end))
                {
                    ranges.Add(markResolver.GetRange(start + 1, end + 1));
                }
            }

            return ranges;
        }

        private static string? FindGlobalNameAtCursor(ExpressionBase expression, string expressionText, OffsetMapper offsetMapper, RequestContext context, MarkResolver markResolver, ref Engine? engine)
        {
            var tokens = TokenizeExpression(expression, context, ref engine, expressionText);
            if (tokens == null)
            {
                return null;
            }

            var orderedTokens = tokens.ToList();
            for (var index = 0; index + 2 < orderedTokens.Count; index++)
            {
                if (!IsIdentifierWithText(orderedTokens[index], expressionText, "Global") || orderedTokens[index + 1].Kind != TokKind.Dot || orderedTokens[index + 2].Kind != TokKind.Ident)
                {
                    continue;
                }

                var nameToken = orderedTokens[index + 2];
                if (offsetMapper.TryMapValueOffsetToFileOffset(nameToken.Span.Min, out var start) && offsetMapper.TryMapValueOffsetToFileOffset(nameToken.Span.Lim, out var end) && context.Index >= start + 1 && context.Index <= end + 1)
                {
                    return expressionText.Substring(nameToken.Span.Min, nameToken.Span.Lim - nameToken.Span.Min);
                }
            }

            return null;
        }

        private static IEnumerable<Token>? TokenizeExpression(ExpressionBase expression, RequestContext context, ref Engine? engine, string expressionText)
        {
            if (engine == null && expression.TryGetCheckResult(context, out _, out var resolvedEngine))
            {
                engine = resolvedEngine;
            }

            return engine?.Tokenize(expressionText);
        }

        private static IEnumerable<Token> EnumerateGlobalNameTokens(IEnumerable<Token> tokens, string expressionText, string variableName)
        {
            var orderedTokens = tokens.Where(token => token.Kind != TokKind.Whitespace).ToList();
            for (var index = 0; index + 2 < orderedTokens.Count; index++)
            {
                if (IsIdentifierWithText(orderedTokens[index], expressionText, "Global") && orderedTokens[index + 1].Kind == TokKind.Dot && IsIdentifierWithText(orderedTokens[index + 2], expressionText, variableName))
                {
                    yield return orderedTokens[index + 2];
                }
            }
        }

        private static bool IsIdentifierWithText(Token token, string expressionText, string expected) => token.Kind == TokKind.Ident && token.Span.Lim <= expressionText.Length && string.Equals(expressionText.Substring(token.Span.Min, token.Span.Lim - token.Span.Min), expected, System.StringComparison.Ordinal);

        private static bool TryGetNameSegmentRange(SyntaxToken valueToken, string variableName, MarkResolver markResolver, out Range range)
        {
            range = Range.Zero;
            var value = valueToken.Value;
            if (value == null)
            {
                return false;
            }

            var prefixIndex = value.IndexOf(GlobalNamespacePrefix, System.StringComparison.Ordinal);
            if (prefixIndex < 0)
            {
                return false;
            }

            var nameValueOffset = prefixIndex + GlobalNamespacePrefix.Length;
            var offsetMapper = valueToken.GetOffsetMapper();
            if (!offsetMapper.TryMapValueOffsetToFileOffset(nameValueOffset, out var start) || !offsetMapper.TryMapValueOffsetToFileOffset(nameValueOffset + variableName.Length, out var end))
            {
                return false;
            }

            range = markResolver.GetRange(start, end);
            return true;
        }

        private static SyntaxNode? GetPropertyValue(MappingObjectSyntax mapping, string propertyName)
        {
            foreach (var property in mapping.AllProperties())
            {
                if (string.Equals(property.PropertyName.Value, propertyName, System.StringComparison.OrdinalIgnoreCase))
                {
                    return property.Value;
                }
            }

            return null;
        }

        private static MarkResolver? GetMarkResolver(McsWorkspace workspace, System.Uri sourceUri) => (workspace.GetDocument(sourceUri.ToFilePath()) as McsLspDocument)?.MarkResolver;
    }
}
