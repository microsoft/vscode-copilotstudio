namespace Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Completion.Handlers
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Resources;
    using System.Collections.Generic;

    internal class UnknownElementHandler : ICompletionEventHandler<EditPropertyValueCompletionEvent>
    {
        private readonly IStringResources _stringResources;

        public UnknownElementHandler(IStringResources stringResources)
        {
            _stringResources = stringResources ?? throw new ArgumentNullException(nameof(stringResources));
        }

        public IEnumerable<CompletionItem> CreateCompletions(RequestContext requestContext, CompletionContext triggerContext, EditPropertyValueCompletionEvent completionEvent)
        {
            var unknown = completionEvent.Element?.SelfOrParentOfType<IUnknown>();
            if (unknown != null)
            {
                Range? range = completionEvent.GetRangeForCurrentToken(requestContext.Document.MarkResolver);

                foreach (var name in unknown.ExpectedKinds)
                {
                    StringResource resource;
                    if (TypeUtility.TryParseBotElementKind(name, out var kind))
                    {
                        resource = _stringResources.GetElementDescription(kind);
                    }
                    else
                    {
                        resource = default;
                    }

                    yield return new CompletionItem
                    {
                        Label = name,
                        TextEdit = range == null ? null : new TextEdit()
                        {
                            NewText = name,
                            Range = range.Value
                        },
                        Detail = resource.Title,
                        Documentation = resource.Description
                    };
                }
            }
        }
    }
}
