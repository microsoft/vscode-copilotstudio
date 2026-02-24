namespace Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Completion.Handlers
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.PowerFx.Intellisense;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using System.Collections.Generic;
    using System.Diagnostics;

    // We will get the expression from the BotElement, not the syntax.
    // CompletionEvent could be propertyValue or sequence. 
    internal class PowerFxCompletionHandler : ICompletionEventHandler<CompletionEvent>
    {
        private readonly ILspLogger _logger;

        public PowerFxCompletionHandler(ILspLogger logger)
        {
            _logger = logger;
        }

        public IEnumerable<CompletionItem> CreateCompletions(RequestContext requestContext, CompletionContext triggerContext, CompletionEvent completionEvent)
        {
            // Intellisense can be an intensive operation  - add logging to track. 
            Stopwatch stopwatch = Stopwatch.StartNew();
            if (completionEvent.Element != null &&
                completionEvent.Element.TryGetExpressionContext(requestContext, out var expressionContext))
            {
                var time1 = stopwatch.ElapsedMilliseconds;
                _logger.LogInformation($"TryGetExpressionContext time={time1}ms");

                stopwatch.Restart();
                var intellisense = expressionContext.GetPowerFxIntellisense(_logger);

                var time2 = stopwatch.ElapsedMilliseconds;
                _logger.LogInformation($"GetPowerFxIntellisense time={time2}ms");

                foreach (var item in intellisense?.Suggestions ?? [])
                {
                    yield return new CompletionItem
                    {
                        Label = item.DisplayText.Text,
                        Detail = item.FunctionParameterDescription,
                        Documentation = item.Definition,
                        Kind = GetCompletionItemKind(item.Kind),
                    };
                }
            }
        }

        private static CompletionKind GetCompletionItemKind(SuggestionKind kind) => kind switch
        {
            SuggestionKind.Function => CompletionKind.Function,
            SuggestionKind.KeyWord => CompletionKind.Keyword,
            SuggestionKind.Global => CompletionKind.Variable,
            SuggestionKind.Field => CompletionKind.Field,
            SuggestionKind.Alias => CompletionKind.Variable,
            SuggestionKind.Enum => CompletionKind.Enum,
            SuggestionKind.BinaryOperator => CompletionKind.Operator,
            SuggestionKind.Local => CompletionKind.Variable,
            SuggestionKind.ServiceFunctionOption => CompletionKind.Function,
            SuggestionKind.Service => CompletionKind.Module,
            SuggestionKind.ScopeVariable => CompletionKind.Variable,
            _ => CompletionKind.Text,
        };
    }
}
