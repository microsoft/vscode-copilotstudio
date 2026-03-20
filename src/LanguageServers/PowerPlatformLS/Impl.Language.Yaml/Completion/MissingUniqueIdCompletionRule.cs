namespace Microsoft.PowerPlatformLS.Impl.Language.Yaml.Completion
{
    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Completion;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;

    internal class MissingUniqueIdCompletionRule : ICompletionRule<YamlLspDocument>
    {
        public IEnumerable<string>? CharacterTriggers { get; } = ["\n"];

        public IEnumerable<CompletionItem> ComputeCompletion(RequestContext requestContext, CompletionContext triggerContext)
        {
            var document = requestContext.Document.As<YamlLspDocument>();
            int index = requestContext.Index;
            var semanticModel = document?.FileModel ?? throw new InvalidOperationException("document semantic is missing");
            var semanticSegment = semanticModel.GetSemanticContextAtIndex(index);

            if (semanticSegment.LastScalarValues[0] == "kind" && semanticSegment.NextScalarValues[0] != "id")
            {
                // TODO : store existing ids and mapping in semantic models and use them to generate unique ids - share with diagnostics
                var randomId = Util.GenerateRandomString(6);
                var kindValue = semanticSegment.LastScalarValues[1] ?? "Unknown";
                var missingIndentation = GetMissingIndentation(document, semanticSegment.SecondLastNode?.Start.Column ?? 0, index);
                var suggestion = $"{missingIndentation}id: {kindValue}_{randomId}";
                yield return new CompletionItem
                {
                    Label = suggestion,
                    Kind = CompletionKind.Text,
                    Detail = "Randomly Generated Id",
                    Documentation = "All entities must have a unique id",
                    SortText = Constants.CompletionItemPriority.P1,
                    InsertText = suggestion,
                };
            }
        }

        private static string GetMissingIndentation(YamlLspDocument document, long kindNodeColumn, int idNodeStartIndex)
        {
            // Compute the size of the white space preceding idNodeStartIndex in text, on the same line
            int idNodeColumn = GetColumnPosition(document.Text, idNodeStartIndex);
            var indentationSize = kindNodeColumn - idNodeColumn;
            if (indentationSize < 0)
            {
                // TODO. Erase indentationSize characters behind idNodeStartIndex may be required.
                return string.Empty;
            }
            else
            {
                return new string(document.IndentationInfo.Character, (int)indentationSize);
            }
        }

        private static int GetColumnPosition(string text, int index)
        {
            int column = 1;
            for (int i = index - 1; i >= 0; i--)
            {
                if (text[i] == '\n')
                {
                    break;
                }
                column++;
            }
            return column;
        }
    }
}