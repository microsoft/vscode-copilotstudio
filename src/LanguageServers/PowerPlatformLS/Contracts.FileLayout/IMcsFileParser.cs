namespace Microsoft.PowerPlatformLS.Contracts.FileLayout
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.Agents.ObjectModel.FileProjection;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models.Lsp;

    public interface IMcsFileParser
    {
        (BotComponentBase? component, Exception? error) CompileFile(
            LspDocument<BotElement> document,
            ProjectionContext context);

        /// <summary>
        /// This method compiles a file model based on the schema name and the BotElement model.
        /// This is useful for compiling content that are not directly associated with a specific document, e.g. merged content from multiple sources.
        /// </summary>
        (BotComponentBase? component, Exception? error) CompileFileModel(string schemaName, BotElement? model);
    }
}
