namespace Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Completion.Handlers
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.Agents.ObjectModel.Serialization;
    using Microsoft.Agents.ObjectModel.Syntax;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Resources;
    using System.Collections.Generic;

    internal class CompletePropertyNameHandler : ICompletionEventHandler<NewPropertyCompletionEvent>
    {
        private readonly IStringResources _stringResources;

        public CompletePropertyNameHandler(IStringResources stringResources)
        {
            _stringResources = stringResources ?? throw new ArgumentNullException(nameof(stringResources));
        }

        public IEnumerable<CompletionItem> CreateCompletions(RequestContext requestContext, CompletionContext triggerContext, NewPropertyCompletionEvent completionEvent)
        {
            var element = completionEvent.Element;
            if (element != null)
            {
                var syntax = completionEvent.CurrentToken;
                Range? range = null;
                if (completionEvent.CurrentToken is SyntaxToken st && !st.IsTrivia)
                {
                    range = completionEvent.GetRangeForCurrentToken(requestContext.Document.MarkResolver);
                }
                

                bool first = true;
                var existingAssignedProperties = new HashSet<string>(completionEvent.Parent?.AllPropertyNames() ?? [], StringComparer.OrdinalIgnoreCase);
                foreach (var property in GetPropertyNames(element, completionEvent).OrderBy(p => p.Priority))
                {
                    var strings = _stringResources.GetPropertyDescription(element.Kind, property.PropertyName);

                    var suffix = property.YamlType switch
                    {
                        PropertyType.MappingObject => ": \n  ",
                        PropertyType.MappingSequence => ": \n  - ",
                        _ => ": ",
                    };

                    yield return new()
                    {
                        Label = property.PropertyName + suffix,
                        TextEdit = range == null ? null : new TextEdit()
                        {
                            NewText = property.PropertyName,
                            Range = range.Value
                        },
                        Detail = strings.Title,
                        Documentation = strings.Description,
                        SortText = property.SortString,
                        InsertTextMode = InsertTextMode.AdjustIndentation,
                        Preselect = first ? true : null,
                        Kind = CompletionKind.Property
                    };

                    first = false;
                }
            }
        }

        private IEnumerable<PropertyNameInfo> GetPropertyNames(BotElement element, NewPropertyCompletionEvent completionEvent)
        {
            foreach (var(propertyName, flags) in BotElementReflection.GetNameTable(element).GetPropertyNames())
            {
                var propertyType = HasFlag(flags, NameTablePropertyFlags.YamlObject) ? PropertyType.MappingObject :
                                   HasFlag(flags, NameTablePropertyFlags.YamlCollection) ? PropertyType.MappingSequence
                                   : PropertyType.Primitive;

                var hasRelevantText = HasRelatedText(propertyName, completionEvent);
                var priorityScore = (HasFlag(flags, NameTablePropertyFlags.IsRequired) ? 1 : 0) + (hasRelevantText ? 2 : 0);
                var priority = 3 - priorityScore;
                yield return new PropertyNameInfo
                {
                    Priority = priority,
                    PropertyName = propertyName,
                    YamlType = propertyType,
                };
            }


            static bool HasFlag(NameTablePropertyFlags flags, NameTablePropertyFlags flagToCheck)
            {
                return (flags & flagToCheck) > 0;
            }
        }

        private bool HasRelatedText(string item, NewPropertyCompletionEvent completionEvent)
        {
            if (completionEvent.PropertyName == null)
            {
                return false;
            }

            if (item.StartsWith(completionEvent.PropertyName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (completionEvent.PropertyName.Length > 1 && item.Contains(completionEvent.PropertyName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        private readonly struct PropertyNameInfo
        {
            public required PropertyType YamlType { get; init; }
            public required string PropertyName { get; init; }
            public required int Priority { get; init; }

            public string SortString => Priority + PropertyName;
        }

        private enum PropertyType
        {
            MappingObject,
            MappingSequence,
            Primitive,
        }
    }
}
