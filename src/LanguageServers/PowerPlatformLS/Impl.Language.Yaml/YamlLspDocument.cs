namespace Microsoft.PowerPlatformLS.Impl.Language.Yaml
{
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models.Lsp;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.CopilotStudio.McsCore.Yaml;
    using Microsoft.PowerPlatformLS.Impl.Language.Yaml.Model;

    internal class YamlLspDocument : LspDocument<YamlSemanticModel>
    {
        public YamlLspDocument(FilePath path, string text, DirectoryPath workspacePath)
            : base(path, text, Constants.LanguageIds.Yaml, workspacePath)
        {
            IndentationInfo = IndentationInfo.FromText(text);
            IsWorkspaceLayoutMarker = WorkspacePath.IsWorkspaceLayoutMarkerFile(path);
        }

        public IndentationInfo IndentationInfo { get; }

        public bool IsWorkspaceLayoutMarker { get; }

        protected override YamlSemanticModel? ComputeModel()
        {
            if (IsWorkspaceLayoutMarker)
            {
                ParsingInfo.Diagnostic = null;
                return null;
            }

            YamlSemanticModel result;
            try
            {
                result = new Model.YamlSemanticModel(Text);
            }
            catch (McsYamlFormatException semanticError)
            {
                ParsingInfo.Diagnostic = new Diagnostic
                {
                    Range = new Contracts.Lsp.Models.Range()
                    {
                        Start = new Position() { Line = semanticError.Line - 1, Character = semanticError.Column - 1 },
                        End = new Position() { Line = semanticError.Line - 1, Character = semanticError.Column - 1 }
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
