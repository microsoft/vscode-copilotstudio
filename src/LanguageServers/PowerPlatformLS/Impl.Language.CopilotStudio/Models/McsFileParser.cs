namespace Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Models
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.Agents.ObjectModel.FileProjection;
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.PowerPlatformLS.Contracts.FileLayout;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models.Lsp;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Exceptions;

    /// <summary>
    /// Thin adapter for LSP operations. Delegates to <see cref="McsFileParserCore"/>.
    /// Wraps errors as <see cref="UnsupportedBotElementException"/> (legacy behavior).
    /// </summary>
    internal class McsFileParser : Contracts.FileLayout.IMcsFileParser
    {
        private readonly LspProjectorService _projectorService = LspProjectorService.Instance;

        public (BotComponentBase? component, Exception? error) CompileFileModel(string schemaName, BotElement? model, string? displayName = null, string? description = null)
        {
            return McsFileParserCore.InternalCompileFile(
                _projectorService, McsFileParserCore.VirtualPath, schemaName, model,
                (msg, element) => new UnsupportedBotElementException(msg, element), displayName, description);
        }

        public (BotComponentBase? component, Exception? error) CompileFile(
            LspDocument<BotElement> document,
            ProjectionContext context)
        {
            BotElement? fileModel = document.FileModel;
            var relativePath = document.As<McsLspDocument>().RelativePath;

            if (fileModel == null)
            {
                return (null, new InvalidDataException($"File model is null for {relativePath}"));
            }

            var schemaName = McsFileParserCore.DeriveSchemaName(_projectorService, fileModel, relativePath, context);

            if (schemaName == null)
            {
                return (null, new UnsupportedBotElementException($"Can't get schema", fileModel));
            }

            try
            {
                var (displayName, description) = McsFileParserCore.GetMetaDataInfo(fileModel, schemaName);
                return McsFileParserCore.InternalCompileFile(
                    _projectorService, relativePath, schemaName, fileModel,
                    (msg, element) => new UnsupportedBotElementException(msg, element), displayName, description);
            }
            catch (Exception ex)
            {
                return (null, ex);
            }
        }
    }
}
