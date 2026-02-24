namespace Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Completion
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.Agents.ObjectModel.Expressions;
    using Microsoft.Agents.ObjectModel.PowerFx;
    using Microsoft.Agents.ObjectModel.Syntax;
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.PowerFx;
    using Microsoft.PowerFx.Intellisense;
    using Microsoft.PowerFx.Types;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Models;
    using System.Diagnostics;
    using System.Diagnostics.CodeAnalysis;

    /// <summary>
    /// Represent a Power Fx expression (as text) and an offset into that text.
    /// Very useful for intellisense operations.
    /// See <see cref="ExpressionContextExtensions"/> helpers for extracting these contexts from various bot elements.
    /// Expression can live in <see cref="ExpressionBase"/> and <see cref="TemplateLine"/>.
    /// </summary>
    internal class ExpressionContext
    {
        /// <summary>
        /// The Power Fx expression text.
        /// This is pure expression, without leading '=' or other decorators.
        /// </summary>
        public required string ExpressionText { get; init; }

        /// <summary>
        /// 0-based offset into <see cref="ExpressionText"/>.
        /// </summary>
        public required int Offset { get; init; }

        /// <summary>
        /// The element containing this expression. 
        /// </summary>
        public required BotElement Element { get; init; }

        /// <summary>
        /// Workspace, for resolving symbols. 
        /// </summary>
        public required McsWorkspace Workspace { get; init; }

        public IIntellisenseResult? GetPowerFxIntellisense(ILspLogger? logger = null)
        {
            if (TryGetCheckResult(logger, out var check, out var engine))
            {
                var suggestionResults = engine.Suggest(check, Offset);
                return suggestionResults;
            }
            return null;
        }

        // Get Power Fx CheckResult from a ExpressionBase. 
        public bool TryGetCheckResult(
            ILspLogger? logger,
            [NotNullWhen(returnValue: true)] out CheckResult? check,
            [NotNullWhen(returnValue: true)] out Engine? engine)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();

            var expressionContext = this;

            check = null;
            engine = null;

            var workspace = expressionContext.Workspace;
            var config = workspace.FeatureConfiguration;

            var semanticModel = workspace.GetSemanticModel(expressionContext.Element);
            var time1 = stopwatch.ElapsedMilliseconds;

            var rdt = semanticModel.GetFullContext(expressionContext.Element);
            var time2 = stopwatch.ElapsedMilliseconds;

            IExpressionCheckerOperationContext operationContext = workspace.GetOperationContext();

            var engineCache = ExpressionEngineProvider.GetEngineCache(operationContext, config);
            var time3 = stopwatch.ElapsedMilliseconds;

            engine = engineCache.GetRecalcEngine();
            var time4 = stopwatch.ElapsedMilliseconds;

            var fxType = rdt.ToFormulaType(operationContext, config);
            var fxRecordType = (RecordType)fxType;

            var time5 = stopwatch.ElapsedMilliseconds;

            logger?.LogInformation($"TryGetCheckResult times={time1},{time2},{time3},{time4},{time5}");

            string text = expressionContext.ExpressionText;
            check = new CheckResult(engine)
                .SetText(text)
                .SetBindingInfo(fxRecordType);

            return true;
        }
    }

    /// <summary>
    /// Helpers for creating ExpressionContext.    
    /// </summary>
    internal static class ExpressionContextExtensions
    {
        // Is this cursor inside a Power Fx expression?
        public static bool TryGetExpressionContext(
            this BotElement element,
            RequestContext requestContext,
            [NotNullWhen(returnValue: true)] out ExpressionContext? expressionContext)
        {
            if (element is ExpressionBase expr)
            {
                return expr.TryGetExpression(requestContext, out expressionContext);
            }
            else if (element is TemplateLine expr2)
            {
                return expr2.TryGetExpression(requestContext, out expressionContext);
            }
            expressionContext = null;
            return false;
        }

        private static bool TryGetExpression(
           this ExpressionBase element,
           RequestContext requestContext,
           [NotNullWhen(returnValue: true)] out ExpressionContext? expressionContext)
        {
            if (element.TryConvertPositionToExpressionOffset(requestContext.Index, out var offset))
            {
                string? expression = element.ExpressionText;
                if (expression != null)
                {
                    expressionContext = new ExpressionContext
                    {
                        ExpressionText = expression,
                        Offset = offset,
                        Element = element,
                        Workspace = (McsWorkspace)requestContext.Workspace
                    };
                    return true;
                }
            }

            expressionContext = null;
            return false;
        }

        // Map the file position to a (expr, offset).
        // 012345678
        // ab{1+2}cd
        private static bool TryGetExpression(
            this TemplateLine element,
            RequestContext requestContext,
            [NotNullWhen(returnValue: true)] out ExpressionContext? expressionContext
        )
        {
            int fileOffset = requestContext.Index;

            if (element.Syntax is SyntaxToken exprToken)
            {
                var (segment, span) = element.GetSegmentsWithSpans().FirstOrDefault(s => s.rawOffsetSpan.Contains(fileOffset));
                if (segment is ExpressionSegment seg && seg.Expression is ExpressionBase expr)
                {
                    var rawExpressionText = expr.ExpressionText ?? expr.VariableReference?.ToString() ?? string.Empty;
                    expressionContext = new ExpressionContext
                    {
                        ExpressionText = rawExpressionText,
                        Offset = fileOffset - span.Start - 1, // skip past '{'
                        Element = element,
                        Workspace = (McsWorkspace)requestContext.Workspace
                    };
                    return true;
                }
            }

            expressionContext = null;
            return false;
        }

    }
}
