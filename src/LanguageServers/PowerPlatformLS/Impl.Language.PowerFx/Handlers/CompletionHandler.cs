
namespace Microsoft.PowerPlatformLS.Impl.Language.PowerFx.Handlers
{
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.PowerFx.Intellisense;
    using Microsoft.PowerFx;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using System.Globalization;
    using System.Threading;
    using System.Threading.Tasks;

    [LspMethodHandler(LspMethods.Completion)]
    internal class CompletionHandler : IRequestHandler<CompletionParams, CompletionList, RequestContext>
    {
        public bool MutatesSolutionState => false;

        public Task<CompletionList> HandleRequestAsync(CompletionParams request, RequestContext context, CancellationToken cancellationToken)
        {
            var document = context.Document.As<PowerFxLspDocument>();
            var checkResult = document.FileModel;
            var index = context.Index;
            var suggestions = new RecalcEngine().Suggest(checkResult, index);
            var precedingCharacter = index != 0 ? document.Text[index - 1] : '\0';

            var items = suggestions.Suggestions.Select((item, index) =>
            {
                return new CompletionItem
                {
                    Label = item.DisplayText.Text,
                    Detail = item.FunctionParameterDescription,
                    Documentation = item.Definition,
                    Kind = GetCompletionItemKind(item.Kind),
                    SortText = index.ToString("D3", CultureInfo.InvariantCulture),
                    InsertText = item.DisplayText.Text is { } label && label[0] == '\'' && precedingCharacter == '\'' ? label.Substring(1) : item.DisplayText.Text
                };
            });

            var completionList = new CompletionList
            {
                IsIncomplete = false,
                Items = items.ToArray(),
            };

            return Task.FromResult(completionList);
        }

        private static CompletionKind GetCompletionItemKind(SuggestionKind kind)
        {
            return kind switch
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
}
