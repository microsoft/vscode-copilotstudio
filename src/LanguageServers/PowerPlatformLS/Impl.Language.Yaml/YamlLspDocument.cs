namespace Microsoft.PowerPlatformLS.Impl.Language.Yaml
{
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models.Lsp;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Impl.Language.Yaml.Model;

    internal class YamlLspDocument : LspDocument<YamlSemanticModel>
    {
        public YamlLspDocument(FilePath path, string text, DirectoryPath workspacePath)
            : base(path, text, Constants.LanguageIds.Yaml, workspacePath)
        {
            IndentationInfo = IndentationInfo.FromText(text);
        }

        public IndentationInfo IndentationInfo { get; }

        protected override YamlSemanticModel? ComputeModel()
        {
            YamlSemanticModel result;
            try
            {
                result = new Model.YamlSemanticModel(Text);
            }
            catch (YamlDotNet.Core.YamlException semanticError)
            {
                var startLineIdx = (int)semanticError.Start.Line - 1;
                var startCharIdx = (int)semanticError.Start.Column - 1;
                var endLineIdx = (int)semanticError.End.Line - 1;
                var endCharId = (int)semanticError.End.Column - 1;
                ParsingInfo.Diagnostic = new Diagnostic
                {
                    Range = new Contracts.Lsp.Models.Range()
                    {
                        Start = new Position() { Line = startLineIdx, Character = startCharIdx },
                        End = new Position() { Line = endLineIdx, Character = endCharId }
                    },
                    Severity = DiagnosticSeverity.Error,
                    Message = semanticError.Message,
                };
                return null;
            }
            catch (Exception parsingError)
            {
                ParsingInfo.Diagnostic = new Diagnostic
                {
                    Range = Range.Zero,
                    Severity = DiagnosticSeverity.Error,
                    Message = $"Failed to compute semantic model. Unhandled exception: {parsingError}"
                };
                return null;
            }

            ParsingInfo.Diagnostic = null;
            return result;
        }
    }
}