namespace Microsoft.PowerPlatformLS.Contracts.Internal.Common.Framework
{
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Completion;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models.Lsp;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;

    /// <summary>
    /// Implementation of <see cref="ICompletionRulesProcessor{DocType}"/>.
    /// </summary>
    internal class CompletionRulesProcessor<DocType> : ICompletionRulesProcessor<DocType>
        where DocType : LspDocument
    {
        private readonly IDictionary<string, IList<ICompletionRule<DocType>>> _characterTriggerToRules;
        private readonly IEnumerable<ICompletionRule<DocType>> _catchAllRules;
        private readonly IEnumerable<ICompletionRule<DocType>> _rules;
        private readonly ILspLogger _logger;

        public CompletionRulesProcessor(IEnumerable<ICompletionRule<DocType>> rules, ILspLogger logger)
        {
            _logger = logger;
            var rulesWithoutCharacterTriggers = new List<ICompletionRule<DocType>>();
            _catchAllRules = rulesWithoutCharacterTriggers;
            _rules = rules;
            _characterTriggerToRules = new Dictionary<string, IList<ICompletionRule<DocType>>>();
            foreach (var rule in rules)
            {
                if (rule.CharacterTriggers == null)
                {
                    rulesWithoutCharacterTriggers.Add(rule);
                    continue;
                }

                foreach (var trigger in rule.CharacterTriggers)
                {
                    if (!_characterTriggerToRules.TryGetValue(trigger, out var rulesForTrigger))
                    {
                        rulesForTrigger = new List<ICompletionRule<DocType>>();
                        _characterTriggerToRules.Add(trigger, rulesForTrigger);
                    }

                    rulesForTrigger.Add(rule);
                }
            }

            TriggerCharacters = _characterTriggerToRules.Keys.ToHashSet();
        }

        public IReadOnlySet<string> TriggerCharacters { get; }

        public IEnumerable<CompletionItem> Run(RequestContext requestContext, CompletionContext? triggerContext)
        {
            if (triggerContext == null)
            {
                _logger.LogWarning("TriggerContext is null. Unable to execute completion rules. Per LSP Specs, this is suggesting initialization error: contextSupport should be set to true. See https://microsoft.github.io/language-server-protocol/specifications/lsp/3.17/specification/#completionParams for details.");
                return [];
            }

            if (requestContext.IsInvalid && requestContext.Document.Text.Length != 0)
            {
                _logger.LogError("RequestContext is invalid. Unable to execute completion rules.");
                return [];
            }

            IEnumerable<ICompletionRule<DocType>> activeRules;
            if (triggerContext.TriggerKind != CompletionTriggerKind.TriggerCharacter)
            {
                activeRules = _rules;
            }
            else
            {
                activeRules = _catchAllRules;
                if (triggerContext.TriggerCharacter == null)
                {
                    _logger.LogError("TriggerCharacter is null in TriggerCharacter completion context. Unable to execute character based rules.");
                }
                else if (_characterTriggerToRules.TryGetValue(triggerContext.TriggerCharacter, out var rulesForTrigger))
                {
                    activeRules = activeRules.Concat(rulesForTrigger);
                }
            }

            return activeRules.SelectMany(rule => rule.ComputeCompletion(requestContext, triggerContext)).ToArray();
        }
    }
}