namespace Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.Agents.ObjectModel.TemplateContent;
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using System;
    using System.Threading;
    using Position = Contracts.Lsp.Models.Position;
    using Range = Contracts.Lsp.Models.Range;

    internal static class CodeActionHelper
    {
        internal static CodeAction[]? GetSuggestions(BotElementDiagnostic diagnostic, Range errorRange, BotElement parentElement)
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

            var allActions = fileActions == null && workspaceActions == null ?
                null :
                (fileActions ?? Enumerable.Empty<CodeAction>()).Concat(workspaceActions ?? Enumerable.Empty<CodeAction>());
            return allActions?.ToArray();
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
