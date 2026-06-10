namespace Microsoft.PowerPlatformLS.Contracts.FileLayout
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.Agents.ObjectModel.FileProjection;
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models.Lsp;

    public interface IMcsFileParser
    {
        (BotComponentBase? component, Exception? error) CompileFile(
            LspDocument<BotElement> document,
            ProjectionContext context);

        /// <summary>
        /// Shape-aware overload of <see cref="CompileFile(LspDocument{BotElement}, ProjectionContext)"/>.
        /// The <paramref name="shape"/> selects the projection rule set used to derive the
        /// component schema name from the file path (TDD D20, D31), so CLI three-layer
        /// <c>.mcs.yml</c> files (<c>behaviors/</c>, <c>capabilities/tools/</c>,
        /// <c>capabilities/knowledge/</c>) resolve to their CLI schema names. The classic
        /// (no-shape) overload keeps the existing behavior byte-identical.
        /// </summary>
        (BotComponentBase? component, Exception? error) CompileFile(
            LspDocument<BotElement> document,
            ProjectionContext context,
            AuthoringShape shape);

        /// <summary>
        /// This method compiles a file model based on the schema name and the BotElement model.
        /// This is useful for compiling content that are not directly associated with a specific document, e.g. merged content from multiple sources.
        /// </summary>
        (BotComponentBase? component, Exception? error) CompileFileModel(string schemaName, BotElement? model, string? displayName, string? description);
    }
}
