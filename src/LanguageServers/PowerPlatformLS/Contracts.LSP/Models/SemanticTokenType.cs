namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Models
{
    /// <summary>
    /// The semantic token types the server uses.
    /// https://microsoft.github.io/language-server-protocol/specifications/lsp/3.17/specification/#semanticTokenTypes
    /// </summary>
    public enum SemanticTokenType
    {
        Keyword,
        Variable,
        Function,
        Interface,
        Comment,
        Namespace,
        Type,
        Class,
        Enum,
        Struct,
        TypeParameter,
        Parameter,
        Property,
        EnumMember,
        Event,
        Method,
        Macro,
        Modifier,
        String,
        Number,
        Regexp,
        Operator,
        Decorator,
        Default
    }
}