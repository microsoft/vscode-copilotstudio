namespace Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Completion
{
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;

    internal interface ICompletionEventHandler<T> where T : CompletionEvent
    {
        IEnumerable<CompletionItem> CreateCompletions(RequestContext requestContext, CompletionContext triggerContext, T completionEvent);
    }

    internal interface ICompletionEventHandler
    {
        bool CanHandle(CompletionEvent completionEvent);

        IEnumerable<CompletionItem> CreateCompletions(RequestContext requestContext, CompletionContext triggerContext, CompletionEvent completionEvent);
    }

    // Adpater to dispatch from a ICompletionEventHandler to a type-safe ICompletionEventHandler<T>.
    internal class CompletionAdapter<EventType, CompletionType> : ICompletionEventHandler
        where EventType : CompletionEvent
        where CompletionType : ICompletionEventHandler<EventType>
    {
        private readonly ICompletionEventHandler<EventType> _handler;

        public CompletionAdapter(CompletionType handler)
        {
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        }

        public bool CanHandle(CompletionEvent completionEvent) => completionEvent is EventType;

        public IEnumerable<CompletionItem> CreateCompletions(RequestContext requestContext, CompletionContext triggerContext, CompletionEvent completionEvent)
        {
            return _handler.CreateCompletions(requestContext, triggerContext, (EventType)completionEvent);
        }
    }
}
