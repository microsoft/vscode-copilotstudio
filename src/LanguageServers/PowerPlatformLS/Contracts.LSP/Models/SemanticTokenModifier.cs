namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Models
{
    /// <summary>
    /// The semantic token modifiers the server uses.
    /// https://microsoft.github.io/language-server-protocol/specifications/lsp/3.17/specification/#semanticTokenModifiers
    /// </summary>
    public enum SemanticTokenModifier
    {
        Declaration,
        Definition,
        Readonly,
        Static,
        Deprecated,
        Abstract,
        Async,
        Modification,
        Documentation,
        DefaultLibrary
    }
}