namespace Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Completion.Handlers
{
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Completion.Generators;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Models;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Resources;
    using Schema = Microsoft.Agents.ObjectModel.Schema;
    using System;
    using System.Collections.Generic;

    internal class EditPropertyValueHandler : ICompletionEventHandler<EditPropertyValueCompletionEvent>
    {
        private readonly IBotElementCompletionGenerator _completionGenerator;
        private readonly IStringResources _stringResources;

        public EditPropertyValueHandler(IBotElementCompletionGenerator botElementCompletionGenerator, IStringResources stringResources)
        {
            _completionGenerator = botElementCompletionGenerator ?? throw new ArgumentNullException(nameof(botElementCompletionGenerator));
            _stringResources = stringResources ?? throw new ArgumentNullException(nameof(stringResources));
        }

        public IEnumerable<CompletionItem> CreateCompletions(RequestContext requestContext, CompletionContext triggerContext, EditPropertyValueCompletionEvent completionEvent)
        {
            var currentElementKind = completionEvent.Element?.Kind;
            var propertyName = completionEvent.PropertyName;

            var syntax = completionEvent.CurrentToken;
            Contracts.Lsp.Models.Range? range = completionEvent.GetRangeForCurrentToken(requestContext.Document.MarkResolver);

            if (currentElementKind != null && propertyName != null
                && _completionGenerator.TryGetPropertyInfo(currentElementKind.Value, propertyName, out var propertyInfo))
            {
                if (propertyInfo.ElementKind == Schema.ElementType.BotElement)
                {
                    foreach (var kind in propertyInfo.Kinds)
                    {
                        if (_completionGenerator.TryGenerateCompletionSnippet(kind, out var snippet) && snippet != null)
                        {
                            var doc = _stringResources.GetElementDescription(kind);
                            yield return new CompletionItem
                            {
                                Kind = CompletionKind.Struct,
                                Label = kind.ToString(),
                                InsertText = range == null ? snippet : null,
                                TextEdit = range == null ? null : new TextEdit()
                                {
                                    NewText = snippet,
                                    Range = range.Value
                                },
                                InsertTextMode = InsertTextMode.AdjustIndentation,
                                SortText = snippet,
                                Detail = doc.Title,
                                Documentation = doc.Description
                            };
                        }
                    }
                }
                else if (propertyInfo.ElementKind == Schema.ElementType.PrimitiveOrEnum)
                {
                    if (_completionGenerator.TryGenerateCompletionSnippets(propertyInfo.PrimitiveKind, (requestContext.Workspace as McsWorkspace)?.CompilationAnalyzer?.RootDefinition, out var snippets))
                    {
                        foreach (var snippet in snippets)
                        {
                            var doc = _stringResources.GetEnumMemberDescription(propertyInfo.PrimitiveKind, snippet);
                            yield return new CompletionItem
                            {
                                Kind = CompletionKind.EnumMember,
                                Label = snippet,
                                Detail = doc.Title,
                                InsertText = range == null ? snippet : null,
                                TextEdit = range == null ? null : new TextEdit()
                                {
                                    NewText = snippet,
                                    Range = range.Value
                                },
                                Documentation = doc.Description
                            };
                        }
                    }
                }
            }
        }
    }
}
