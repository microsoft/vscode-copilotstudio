namespace Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.Agents.ObjectModel.Syntax;
    using Microsoft.Agents.ObjectModel.TemplateContent;
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Utilities;
    using System;
    using System.Text.RegularExpressions;
    using System.Threading;
    using Position = Contracts.Lsp.Models.Position;
    using Range = Contracts.Lsp.Models.Range;

    internal static class CodeActionHelper
    {
        private static readonly Regex GlobalReferenceRegex = new(@"Global\.([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);

        internal static CodeAction[]? GetSuggestions(BotElementDiagnostic diagnostic, Range errorRange, BotElement parentElement, MarkResolver markResolver, Uri? agentRootUri = null)
        {
            parentElement = parentElement is BotComponentBase ? parentElement.Descendants().First() : parentElement;
            Uri ? sourceUri = parentElement.Syntax?.SourceUri;
            if (sourceUri == null)
            {
                return null;
            }

            // in-file edits
            IEnumerable<NamedEdits>? edits = null;
            switch (diagnostic)
            {
                case IncorrectTypeError invalidVariableTypeError:
                    edits = GetEditSuggestions(invalidVariableTypeError, errorRange);
                    break;
                case DuplicateVariableInitializer duplicateVariableInitializer:
                    edits = GetEditSuggestions(duplicateVariableInitializer, errorRange);
                    break;
                case DuplicatePropertyError propError:
                    if (propError.ErrorCode?.Value == ValidationErrorCode.DuplicateActionId)
                    {
                        edits = GetEditSuggestionsForDuplicateId(propError, parentElement, errorRange);
                    }
                    break;
            }

            var fileActions = edits?.ToCodeActions(sourceUri);

            // workspace actions
            IEnumerable<CodeAction>? workspaceActions = null;
            switch (diagnostic)
            {
                case PropertyLengthTooLong stringLengthError when stringLengthError.PropertyName == "SchemaName":
                    workspaceActions = [GetNewValidFilenameSuggestions(parentElement, sourceUri)];
                    break;

                case McsWorkspaceSchemaNameContainsInvalidChars invalidCharsError when invalidCharsError.PropertyName == "SchemaName":
                    workspaceActions = [GetNewValidFilenameWithoutInvalidCharsSuggestions(parentElement, sourceUri)];
                    break;
            }

            IEnumerable<CodeAction>? globalVariableActions = null;
            if (diagnostic is ExpressionError expressionError && expressionError.ErrorCode?.Value == ExpressionErrorCode.IdentifierNotRecognized)
            {
                globalVariableActions = GetGlobalVariableSuggestions(parentElement, errorRange, markResolver, agentRootUri);
            }

            if (fileActions == null && workspaceActions == null && globalVariableActions == null)
            {
                return null;
            }

            return (fileActions ?? Enumerable.Empty<CodeAction>()).Concat(workspaceActions ?? Enumerable.Empty<CodeAction>()).Concat(globalVariableActions ?? Enumerable.Empty<CodeAction>()).ToArray();
        }

        private static CodeAction GetNewValidFilenameWithoutInvalidCharsSuggestions(BotElement parentElement, Uri sourceUri)
        {
            var filenamePath = sourceUri.ToFilePath();
            var sanitizedFilename = Path.Join(SearchSummarizationContentProcessor.SchemaNameRegex.Replace(filenamePath.FileNameWithoutExtension, ""));

            var suggestedUri = ChangeFileName(sourceUri, sanitizedFilename);
            return new CodeAction
            {
                Title = $"Rename file to '{sanitizedFilename + WorkspacePath.GetExtension(filenamePath)}'",
                Kind = CodeActionKind.QuickFix,
                Edit = new WorkspaceEdit
                {
                    DocumentChanges = [new RenameFile
                        {
                            NewUri = suggestedUri,
                            OldUri = sourceUri,
                        }],
                },
            };
        }

        private static CodeAction GetNewValidFilenameSuggestions(BotElement parentElement, Uri sourceUri)
        {
            const int SuggestedSchemaNameLength = 90;
            const int RandomSuffixLength = 4;
            const string Alphanumeric = "abcdefghjklmnpqrstvwxyz23456789";

            var prop = parentElement.GetType().GetProperty("SchemaName");
            string? schemaName = null;
            if (prop != null && prop.CanRead)
            {
                schemaName = (prop.GetValue(parentElement) as IIdentifier)?.ToString();
            }

            // Generate 4 random alphanumeric characters to deduplicate
            var random = new Random();
            string randomSuffix = new string(Enumerable.Range(0, RandomSuffixLength)
                .Select(_ => Alphanumeric[random.Next(Alphanumeric.Length)])
                .ToArray());

            string newFileName;
            if (schemaName?.Length > SuggestedSchemaNameLength && schemaName.LastIndexOf('.') < SuggestedSchemaNameLength - RandomSuffixLength)
            {
                // Take the first characters
                string prefix = schemaName.Substring(0, SuggestedSchemaNameLength - RandomSuffixLength);

                // Split on dot and take the last part (e.g., file extension or suffix)
                string prefixLastPart = prefix.Split('.')[^1];

                // Combine into new file name
                newFileName = $"{prefixLastPart}{randomSuffix}";
            }
            else
            {
                newFileName = $"BotElement{randomSuffix}";
            }

            var suggestedUri = ChangeFileName(sourceUri, newFileName);
            return new CodeAction
            {
                Title = $"Rename file to '{newFileName}'",
                Kind = CodeActionKind.QuickFix,
                Edit = new WorkspaceEdit
                {
                    DocumentChanges = [new RenameFile
                        {
                            NewUri = suggestedUri,
                            OldUri = sourceUri,
                        }],
                },
            };
        }

        private static Uri ChangeFileName(Uri sourceUri, string newFileName)
        {
            var sourceFilepath = sourceUri.ToFilePath();
            var sourceExtension = WorkspacePath.GetExtension(sourceFilepath);
            var parentDirectoryPath = sourceFilepath.ParentDirectoryPath;
            var newFilePath = parentDirectoryPath.GetChildFilePath($"{newFileName}{sourceExtension}");
            return new Uri(newFilePath.ToString());
        }

        private static IEnumerable<NamedEdits>? GetEditSuggestionsForDuplicateId(DuplicatePropertyError propError, BotElement parentElement, Range errorRange)
        {
            var elementKind = parentElement.Kind;
            yield return new(Resources.CodeAction.GenerateNewIdentifier, [
                new TextEdit
                {
                    Range = errorRange,
                    NewText = $"{elementKind}_{GenerateRandomString()}",
                },
            ]);
        }

        private static IEnumerable<NamedEdits> GetEditSuggestions(IncorrectTypeError diagnostic, Range errorRange)
        {
            if (errorRange.Equals(Range.Zero))
            {
                yield break;
            }

            foreach (var suggestion in diagnostic.Suggestions)
            {
                // TODO: Replicate pattern from CompletionHandler as we grow the number of quickfixes.
                // Replicate pattern from CompletionHandler as we grow the number of quickfixes that we emit.
                if (suggestion is ChangeTypeSuggestion changeTypeSugg)
                {
                    var newVarSuffix = changeTypeSugg.AssignedType?.ToString() ?? "Any";
                    yield return new(string.Format(Resources.CodeAction.ChangeVariableNameForArg, newVarSuffix), [
                        new TextEdit
                        {
                            // insert at the end of the range
                            Range = new Range { Start = errorRange.End, End = errorRange.End },
                            NewText = newVarSuffix,
                        },
                    ]);
                }
            }

            yield return CreateNewVariableEdit(errorRange, true, diagnostic.AssignedType?.ToString());
        }

        private static IEnumerable<NamedEdits> GetEditSuggestions(DuplicateVariableInitializer duplicateVariableInitializer, Range errorRange)
        {
            if (errorRange.Equals(Range.Zero))
            {
                yield break;
            }

            const int InitModifierLenght = 5;
            yield return new (Resources.CodeAction.RemoveInitializer, [
                new TextEdit
                {
                    Range = new Range
                    {
                        Start = errorRange.Start,
                        End = new Position { Line = errorRange.Start.Line, Character = errorRange.Start.Character + InitModifierLenght }
                    },
                    NewText = string.Empty,
                },
            ]);

            if (duplicateVariableInitializer.Variable == null)
            {
                yield break;
            }

            var newVariableRange = new Range
            {
                Start = new Position { Line = errorRange.Start.Line, Character = errorRange.Start.Character + InitModifierLenght },
                End = errorRange.End,
            };
            var newVariableEdit = CreateNewVariableEdit(duplicateVariableInitializer.Variable, newVariableRange);
            if (newVariableEdit != null)
            {
                yield return newVariableEdit;
            }
        }

        private static NamedEdits? CreateNewVariableEdit(PropertyPath variable, Range newVariableRange)
        {
            var isTopic = variable.IsTopicVariableReference(out _);
            var isGlobal = !isTopic && variable.IsGlobalVariableReference(out _);
            if (isTopic || isGlobal)
            {
                return CreateNewVariableEdit(newVariableRange, isTopic);
            }

            return null;
        }

        private static NamedEdits CreateNewVariableEdit(Range newVariableRange, bool isTopicVariable, string? varQualifier = null)
        {
            var newVariableName = GenerateRandomVariableName(varQualifier);
            newVariableName = (isTopicVariable ? PropertyPath.TopicVariable(newVariableName) : PropertyPath.GlobalVariable(newVariableName)).ToString();
            return new(Resources.CodeAction.CreateNewVariable, [
                new TextEdit
                    {
                        Range = newVariableRange,
                        NewText = newVariableName,
                    },
                ]);
        }

        private static IEnumerable<CodeAction> ToCodeActions(this IEnumerable<NamedEdits> values, Uri sourceUri)
        {
            return values.Select(x => new CodeAction
            {
                Title = x.Title,
                Kind = CodeActionKind.QuickFix,
                Edit = new WorkspaceEdit
                {
                    DocumentChanges =
                    [
                        new TextDocumentEdit
                        {
                            TextDocument = new VersionedTextDocumentIdentifier
                            {
                                Uri = sourceUri,
                            },
                            Edits = x.Edits
                        }
                    ]
                }
            });
        }

        private static readonly ThreadLocal<Random> Random = new ThreadLocal<Random>(() => new Random());

        private static string GenerateRandomVariableName(string? varQualifier = null)
        {
            return $"My{varQualifier ?? string.Empty}Var{GenerateRandomString()}";
        }

        private static string GenerateRandomString(int length = 4)
        {
            const string Chars = "0123456789";
            // ! Random definition
            return new string(Enumerable.Range(0, length)
                .Select(_ => Chars[Random.Value!.Next(Chars.Length)])
                .ToArray());
        }

        private static IEnumerable<CodeAction> GetGlobalVariableSuggestions(BotElement parentElement, Range errorRange, MarkResolver markResolver, Uri? agentRootUri)
        {
            if (parentElement.Syntax is not SyntaxToken expressionToken || expressionToken.Value is not string expressionValue || expressionToken.SourceUri is not Uri sourceUri)
            {
                yield break;
            }

            if (!TryGetErroredGlobalReference(expressionToken, expressionValue, errorRange, markResolver, out var variableName, out var nameRange) || !RenameEditFactory.IsValidNewName(variableName))
            {
                yield break;
            }

            var botDefinition = parentElement.ParentOfType<BotDefinition>();

            var newFileUri = ResolveNewVariableFileUri(botDefinition, agentRootUri, variableName);
            if (newFileUri != null && !GlobalVariableExists(botDefinition, variableName))
            {
                yield return CreateGlobalVariableAction(variableName, newFileUri, botDefinition, parentElement, markResolver, sourceUri);
            }

            foreach (var existingName in FindCloseGlobalVariableNames(botDefinition, variableName))
            {
                yield return ChangeToExistingVariableAction(existingName, nameRange, sourceUri);
            }
        }

        private static bool TryGetErroredGlobalReference(SyntaxToken expressionToken, string expressionValue, Range errorRange, MarkResolver markResolver, out string variableName, out Range nameRange)
        {
            variableName = string.Empty;
            nameRange = Range.Zero;
            Range? closestRange = null;
            var closestDistance = long.MaxValue;
            foreach (Match match in GlobalReferenceRegex.Matches(expressionValue))
            {
                if (!TryGetValueRange(expressionToken, match.Groups[1].Index, match.Groups[1].Length, markResolver, out var candidateRange))
                {
                    continue;
                }

                if (!RangesOverlap(candidateRange, errorRange))
                {
                    continue;
                }

                var distance = RangeDistance(candidateRange, errorRange);
                if (distance < closestDistance)
                {
                    closestDistance = distance;
                    closestRange = candidateRange;
                    variableName = match.Groups[1].Value;
                }
            }

            if (closestRange == null)
            {
                return false;
            }

            nameRange = closestRange.Value;
            return true;
        }

        private static long RangeDistance(Range candidate, Range target) => (Math.Abs((long)candidate.Start.Line - target.Start.Line) * 100000L) + Math.Abs((long)candidate.Start.Character - target.Start.Character);

        private static bool RangesOverlap(Range first, Range second) => !IsBefore(first.End, second.Start) && !IsBefore(second.End, first.Start);

        private static bool IsBefore(Position first, Position second) => first.Line < second.Line || (first.Line == second.Line && first.Character < second.Character);

        private static bool TryGetValueRange(SyntaxToken token, int valueOffset, int length, MarkResolver markResolver, out Range range)
        {
            range = Range.Zero;
            var offsetMapper = token.GetOffsetMapper();
            if (!offsetMapper.TryMapValueOffsetToFileOffset(valueOffset, out var start) || !offsetMapper.TryMapValueOffsetToFileOffset(valueOffset + length, out var end))
            {
                return false;
            }

            range = markResolver.GetRange(start, end);
            return true;
        }

        private static Uri? ResolveNewVariableFileUri(BotDefinition? botDefinition, Uri? agentRootUri, string variableName)
        {
            if (agentRootUri != null)
            {
                var baseUri = agentRootUri.ToString();
                if (!baseUri.EndsWith("/", StringComparison.Ordinal))
                {
                    baseUri += "/";
                }

                return new Uri($"{baseUri}{GlobalVariableReferenceService.VariablesFolder}{variableName}.mcs.yml");
            }

            var existingGlobalUri = botDefinition?.DescendantsAndSelf().OfType<GlobalVariableComponent>()
                .Select(component => component.Variable?.Syntax?.SourceUri)
                .FirstOrDefault(uri => uri != null);
            return existingGlobalUri != null ? ChangeFileName(existingGlobalUri, variableName) : null;
        }

        private static CodeAction CreateGlobalVariableAction(string variableName, Uri newFileUri, BotDefinition? botDefinition, BotElement parentElement, MarkResolver markResolver, Uri sourceUri)
        {
            var arguments = new CreateGlobalVariableCommandArgs
            {
                DocumentUri = sourceUri.ToString(),
                VariableName = variableName,
                NewFileUri = newFileUri.ToString(),
                FileContent = BuildVariableFileContent(botDefinition, variableName),
                SetVariable = BuildSetVariableInsertion(variableName, parentElement, markResolver),
            };

            return new CodeAction
            {
                Title = $"Create global variable '{variableName}'",
                Kind = CodeActionKind.QuickFix,
                Command = new LspCommand
                {
                    Title = $"Create global variable '{variableName}'",
                    Command = "microsoft-copilot-studio.createGlobalVariable",
                    Arguments = [arguments],
                },
            };
        }

        private static SetVariableInsertion? BuildSetVariableInsertion(string variableName, BotElement parentElement, MarkResolver markResolver)
        {
            if (!TryGetTopicActionsInsertion(parentElement, markResolver, out var insertionRange, out var dashIndent))
            {
                return null;
            }

            var action = new SetVariable.Builder
            {
                Id = $"setVariable_{GenerateRandomString(6)}",
                Variable = PropertyPath.GlobalVariable(variableName),
            }.Build();

            return new SetVariableInsertion
            {
                Line = insertionRange.Start.Line,
                Character = insertionRange.Start.Character,
                TextBeforeValue = $"{IndentAsListItem(CodeSerializer.Serialize(action), dashIndent)}\n{dashIndent}  value: ",
                TextAfterValue = "\n",
            };
        }

        private static string IndentAsListItem(string actionYaml, string dashIndent) => string.Join("\n", actionYaml.Replace("\r\n", "\n").TrimEnd('\n').Split('\n').Select((line, index) => index == 0 ? $"{dashIndent}- {line}" : $"{dashIndent}  {line}"));

        private static string BuildVariableFileContent(BotDefinition? botDefinition, string variableName)
        {
            var botSchema = botDefinition?.Entity?.SchemaName.ToString();
            var schemaName = string.IsNullOrEmpty(botSchema)
                ? $"agent{GlobalVariableReferenceService.GlobalVariableInfix}{variableName}"
                : $"{botSchema}{GlobalVariableReferenceService.GlobalVariableInfix}{variableName}";

            var metadata = new RecordDataValue.Builder();
            metadata.Properties["componentName"] = StringDataValue.Create(variableName);
            var extensionData = new RecordDataValue.Builder();
            extensionData.Properties["mcs.metadata"] = metadata;

            var component = new GlobalVariableComponent.Builder
            {
                SchemaName = schemaName,
                Variable = new Variable.Builder
                {
                    ExtensionData = extensionData,
                    Name = variableName,
                    Scope = VariableScope.Conversation,
                    IsExternalInitializationAllowed = false,
                    IsOutputToExternalCallers = false,
                    InitializationTimeoutInMilliseconds = 0,
                },
            }.Build();

            using var writer = new StringWriter();
            CodeSerializer.SerializeAsMcsYml(writer, component);
            return writer.ToString();
        }

        private static bool TryGetTopicActionsInsertion(BotElement parentElement, MarkResolver markResolver, out Range insertionRange, out string dashIndent)
        {
            insertionRange = Range.Zero;
            dashIndent = string.Empty;

            var actionScope = parentElement.ParentOfType<TriggerBase>()?.Actions;
            if (actionScope == null || actionScope.Actions.IsDefaultOrEmpty)
            {
                return false;
            }

            var firstActionSyntax = actionScope.Actions[0].Syntax;
            if (firstActionSyntax == null)
            {
                return false;
            }

            var contentPosition = markResolver.GetPosition(firstActionSyntax.Position);
            var lineStart = new Position { Line = contentPosition.Line, Character = 0 };
            insertionRange = new Range { Start = lineStart, End = lineStart };
            dashIndent = new string(' ', Math.Max(contentPosition.Character - 2, 0));
            return true;
        }


        private static CodeAction ChangeToExistingVariableAction(string existingName, Range nameRange, Uri sourceUri) => new CodeAction
        {
            Title = $"Change to '{existingName}'",
            Kind = CodeActionKind.QuickFix,
            Edit = new WorkspaceEdit
            {
                DocumentChanges =
                [
                    new TextDocumentEdit
                    {
                        TextDocument = new VersionedTextDocumentIdentifier { Uri = sourceUri },
                        Edits = [new TextEdit { Range = nameRange, NewText = existingName }],
                    },
                ],
            },
        };

        private static IEnumerable<string> FindCloseGlobalVariableNames(BotDefinition? botDefinition, string variableName)
        {
            if (botDefinition == null)
            {
                return Enumerable.Empty<string>();
            }

            return botDefinition.DescendantsAndSelf().OfType<GlobalVariableComponent>()
                .Select(component => GlobalVariableReferenceService.GetVariableName(component.SchemaNameString))
                .Where(candidate => !string.IsNullOrEmpty(candidate) && !string.Equals(candidate, variableName, StringComparison.Ordinal))
                .Select(candidate => candidate!)
                .Distinct()
                .Select(candidate => (Name: candidate, Distance: LevenshteinDistance(candidate, variableName)))
                .Where(candidate => candidate.Distance <= 3 && candidate.Distance < variableName.Length)
                .OrderBy(candidate => candidate.Distance)
                .ThenBy(candidate => candidate.Name, StringComparer.Ordinal)
                .Select(candidate => candidate.Name);
        }

        private static bool GlobalVariableExists(BotDefinition? botDefinition, string variableName) => botDefinition?.DescendantsAndSelf().OfType<GlobalVariableComponent>().Any(component => string.Equals(GlobalVariableReferenceService.GetVariableName(component.SchemaNameString), variableName, StringComparison.Ordinal)) ?? false;

        private static int LevenshteinDistance(string source, string target)
        {
            var distances = new int[source.Length + 1, target.Length + 1];
            for (var i = 0; i <= source.Length; i++)
            {
                distances[i, 0] = i;
            }

            for (var j = 0; j <= target.Length; j++)
            {
                distances[0, j] = j;
            }

            for (var i = 1; i <= source.Length; i++)
            {
                for (var j = 1; j <= target.Length; j++)
                {
                    var substitutionCost = source[i - 1] == target[j - 1] ? 0 : 1;
                    distances[i, j] = Math.Min(Math.Min(distances[i - 1, j] + 1, distances[i, j - 1] + 1), distances[i - 1, j - 1] + substitutionCost);
                }
            }

            return distances[source.Length, target.Length];
        }

        private class NamedEdits
        {
            public NamedEdits(string? title, TextEdit[] edits)
            {
                Title = title ?? string.Empty;
                Edits = edits;
            }

            public string Title { get; }
            public TextEdit[] Edits { get; }
        }
    }
}
