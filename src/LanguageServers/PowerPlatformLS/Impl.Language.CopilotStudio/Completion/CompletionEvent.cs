namespace Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Completion
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.Agents.ObjectModel.Syntax;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Utilities;

    /// <summary>
    /// Base class for different kind of completition events.
    /// </summary>
    internal abstract record CompletionEvent
    {
        /// <summary>
        /// The element being edited.
        /// This is rooted in a BotDefinition and can be used for semantic operations.
        /// </summary>
        public BotElement? Element { get; init; }

        /// The syntax node for the property being modified
        public required SyntaxNode? CurrentToken { get; init; }

        private Range? Range { get; set; }

        public Range? GetRangeForCurrentToken(MarkResolver resolver)
        {
            if (Range != null)
            {
                return Range;
            }

            if (CurrentToken == null)
            {
                return null;
            }

            int? start = null;
            int? end = null;
            if (CurrentToken is SyntaxToken st && st.IsTrivia)
            {
                if (CurrentToken.Parent != null)
                {
                    // You are in the middle of some whitespace. Replace the entire line right of this object
                    start = CurrentToken.Position;
                    // Dont replace the separator
                    if (st.Kind == Agents.ObjectModel.Syntax.Tokens.SyntaxTokenKind.KeyValueSeparator)
                    {
                        start++;
                    }
                    // leave any newlines
                    end = CurrentToken.Parent.EndPosition - 1;
                }
            }
            else
            {
                start = CurrentToken.Position;
                end = CurrentToken.EndPosition;
            }

            if ((start != null && end != null) && start < end)
            {
                return new Contracts.Lsp.Models.Range()
                {
                    Start = resolver.GetPosition(start.Value),
                    End = resolver.GetPosition(end.Value)
                };
            }

            return null;
        }
    }


    internal record SequenceValueCompletionEvent : CompletionEvent
    {
        public int Index { get; init; }
    }

    internal abstract record PropertyCompletionEvent : CompletionEvent
    {
        /// <summary>
        /// The parent that we're adding the new property too. 
        /// </summary>
        public required MappingObjectSyntax? Parent { get; init; }
    }

    /// <summary>
    /// Start of line or mid property name. Suggest a new property
    /// </summary>
    internal record NewPropertyCompletionEvent : PropertyCompletionEvent
    {
        public string? PropertyName => Syntax switch
        {
            IMappingKeyValueSyntax kvp => kvp.PropertyName.Value,
            ErrorSyntax error when error.LastSlot?.LastSlot is SyntaxToken token => token.Value,
            _ => null,
        };

        public SyntaxNode? Syntax { get; init; }
    }

    // Editing the value of a property 
    internal record EditPropertyValueCompletionEvent : PropertyCompletionEvent
    {
        // The property being edited. 
        public required string PropertyName { get; init; }
    }
}