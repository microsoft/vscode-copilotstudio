namespace Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Models
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.Agents.ObjectModel.Yaml;
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.PowerPlatformLS.Contracts.FileLayout;
    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models.Lsp;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Exceptions;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Text.Json;

    [DebuggerDisplay("McsLspDocument: {RelativePath}")]
    internal class McsLspDocument : LspDocument<BotElement>
    {
        public McsLspDocument(FilePath path, string text, DirectoryPath workspacePath)
            : base(path, text, Constants.LanguageIds.CopilotStudio, workspacePath)
        {
            RelativePath = new AgentFilePath(path.GetRelativeTo(workspacePath));
        }

        public AgentFilePath RelativePath { get; }

        public bool IsIcon => RelativePath.IsIcon();

        protected override BotElement? ComputeModel()
        {
            BotElement? syntax = null;

            var completeRelativePath = RelativePath.RemoveExtension();
            if (RelativePath.IsDefinition())
            {
                using (YamlSerializationContext.UseYamlPassThroughSerializationContext())
                {
                    return JsonSerializer.Deserialize<DefinitionBase>(Text, ElementSerializer.CreateOptions());
                }
            }
            else if (RelativePath.TryGetSubAgentName(out var agentName, out var subPathRef))
            {
                completeRelativePath = subPathRef.Value;
                // If this is file directly in the agent subdir, it must be the agent dialog.
                // For sub agents, these is named based on schema name.
                if (!completeRelativePath.IsInSubDir)
                {
                    syntax = DeserializeAs(typeof(AgentDialog));
                    return syntax;
                }
            }

            var completeRelativePathValue = completeRelativePath.ToString();
            if (!LspProjectionLayout.FileStructureMap.TryGetValue(completeRelativePathValue, out var types))
            {
                var directory = completeRelativePath.ParentDirectoryName;
                if (!LspProjectionLayout.FileStructureMap.TryGetValue(directory, out types))
                {
                    // either there is no folder opened or the file name is not supported
                    // try to deserialize as an abstract class to resolve the type
                    return DeserializeAsBotElementWithDiagnostic();
                }
            }

            if (!types.Any())
            {
                // no model type registered for this file, file model is null
                return null;
            }

            var type = types.First(); // Subsequent types were handled.                        

            syntax = DeserializeAs(type);
            return syntax;
        }

        private BotElement? DeserializeAsBotElementWithDiagnostic()
        {
            var fileModel = DeserializeAs(typeof(BotElement));
            if (fileModel == null)
            {
                // File type unidentified
                return null;
            }

            var fileModelType = fileModel.GetType();
            var type = fileModelType;

            if (!LspProjectionLayout.TypeToFileCandidates.TryGetValue(type, out var fileCandidates))
            {
                bool isTypeSupported = false;
                while ((type = type.BaseType) != null && !(isTypeSupported = LspProjectionLayout.TypeToFileCandidates.TryGetValue(type, out fileCandidates)))
                {
                    // continue searching up the inheritance tree
                }

                if (!isTypeSupported)
                {
                    // allow to parse arbitrary BotElement but inform user that it won't be "compiled" under the BotDefinition
                    ParsingInfo.Diagnostic = new Diagnostic
                    {
                        Message = $"Element type '{fileModelType}' is not supported in MCS workspace.",
                        Range = Range.Zero,
                        Severity = DiagnosticSeverity.Warning,
                    };
                    return fileModel;
                }
            }

            // Debug.Assert : TypeToFileCandidates.TryGetValue(type, out var fileCandidates) returned true last
            Debug.Assert(fileCandidates != null);
            Debug.Assert(type != null);
            string expectedLocation = string.Join(" or ", fileCandidates.Select(path => path.EndsWith('/') ? $"the '{path.TrimEnd('/')}' folder" : $"'{path}.mcs.yml'"));
            expectedLocation = fileCandidates.Count > 1 ? "either " + expectedLocation : expectedLocation;

            var data = new DiagnosticData
            {
                Quickfix = GetFileRenameActions(fileCandidates),
            };
            ParsingInfo.Diagnostic = new Diagnostic
            {
                Message = $"Elements of type '{type.Name}' are expected in {expectedLocation}.",
                Range = Range.Zero,
                Severity = DiagnosticSeverity.Warning,
                Code = Constants.ErrorCodes.WrongLocationForEntityType,
                Data = data,
            };

            return fileModel;
        }

        private CodeAction[]? GetFileRenameActions(IReadOnlyCollection<string> pathCandidates)
        {
            return pathCandidates.Select(path =>
            {

                Uri newUri;
                if (path.EndsWith('/'))
                {
                    newUri = new Uri(_workspacePath.GetChildFilePath(path + FilePath.FileName).ToString());
                }
                else
                {
                    newUri = new Uri(_workspacePath.GetChildFilePath(path + ".mcs.yml").ToString());
                }

                return new CodeAction
                {
                    Title = $"Move to '{path}'",
                    Kind = CodeActionKind.QuickFix,
                    Edit = new WorkspaceEdit
                    {
                        DocumentChanges = [new RenameFile
                        {
                            NewUri = newUri,
                            OldUri = Uri,
                        }],
                    },
                };
            }).ToArray();
        }

        private BotElement? DeserializeAs(Type type)
        {
            BotElement? syntax = null;
            try
            {
                syntax = CodeSerializer.Deserialize(Text, type, Uri);
            }
            catch (YamlReaderException omParsingError)
            {
                ParsingInfo.Diagnostic = GetDiagnosticFromException(omParsingError);
                return null;
            }
            catch (Exception parsingError)
            {
                ParsingInfo.Diagnostic = GetDiagnosticFromException(parsingError);
                return null;
            }

            if (syntax == null)
            {
                ParsingInfo.Diagnostic = GetDiagnosticFromException(new EmptyBotElementException());
                return null;
            }

            ParsingInfo.Diagnostic = null;
            return syntax;
        }

        private static Diagnostic? GetDiagnosticFromException(Exception parsingError)
        {
            bool isExpected = parsingError is UnsupportedBotElementException || parsingError is EmptyBotElementException;

            // ! null is not Exception
            var message = isExpected ? parsingError.Message : $"Failed to compute semantic model. Unhandled exception: {parsingError}";
            return new Diagnostic
            {
                Range = Range.Zero,
                Severity = DiagnosticSeverity.Error,
                Message = message
            };
        }

        private static Diagnostic GetDiagnosticFromException(YamlReaderException omParsingError)
        {
            var lineIndex = omParsingError.Location.Line - 1;
            var startCharacterIndex = Math.Max(0, omParsingError.Location.Column - 2);
            var endColumnIndex = Math.Max(0, omParsingError.Location.Column - 1);
            return new Diagnostic
            {
                Range = new Range
                {
                    Start = new Position { Line = lineIndex, Character = startCharacterIndex },
                    End = new Position { Line = lineIndex, Character = endColumnIndex },
                },
                Message = omParsingError.Message,
                Severity = DiagnosticSeverity.Error,
            };
        }
    }
}