namespace Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Completion
{
    using Microsoft.PowerPlatformLS.Contracts.Internal.Completion;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Models;

    // Main MCS completition rule. This will trigger other complettion handlers. . 
    internal class CopilotStudioCompletionRule : ICompletionRule<McsLspDocument>
    {
        private readonly IEnumerable<ICompletionEventHandler> _completionEventHandlers;

        // Triggers in addition to CapabilitiesProvider.AdditionalTriggerCharacters.
        public IEnumerable<string>? CharacterTriggers => [".", ":", " ", "\n"];

        public CopilotStudioCompletionRule(IEnumerable<ICompletionEventHandler> completionEventHandlers)
        {
            _completionEventHandlers = completionEventHandlers;
        }

        public IEnumerable<CompletionItem> ComputeCompletion(RequestContext requestContext, CompletionContext triggerContext)
        { 
            var intellisenseEvent = requestContext.Triage(triggerContext);
            if (intellisenseEvent != null)
            {
                foreach (var handler in _completionEventHandlers)
                {
                    if (handler.CanHandle(intellisenseEvent))
                    {
                        foreach (var item in handler.CreateCompletions(requestContext, triggerContext, intellisenseEvent))
                        {
                            yield return item;
                        }
                    }
                }
            }
        }
    }
}
