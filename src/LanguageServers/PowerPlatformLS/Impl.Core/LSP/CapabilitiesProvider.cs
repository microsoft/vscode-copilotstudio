namespace Microsoft.PowerPlatformLS.Impl.Core.Lsp
{
    using Microsoft.PowerPlatformLS.Contracts.Internal.Completion;

    internal class CapabilitiesProvider : ICapabilitiesProvider
    {
        /// <summary>
        /// This is a list of trigger characters that are not specific to any existing completion rules.
        /// They are needed for completion rules that are not bound to a set of trigger character(s).
        /// Note that any characters specified here adds to what is specified by the client, typically [a-zA-Z], per <see cref="Contracts.Lsp.Models.CompletionOptions"/>.
        /// Those characters don't need to be added explicitly.
        /// </summary>
        private static readonly string[] AdditionalTriggerCharacters = [":", " "];

        public IReadOnlySet<string> TriggerCharacters { get; }

        public CapabilitiesProvider(IEnumerable<ICompletionRulesProcessor> completionRulesProcessors)
        {
            TriggerCharacters = completionRulesProcessors.SelectMany(x => x.TriggerCharacters).Concat(AdditionalTriggerCharacters).ToHashSet();
        }
    }
}