namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Models
{
    /// <summary>
    /// See https://microsoft.github.io/language-server-protocol/specifications/lsp/3.17/specification/#codeActionKind
    /// </summary>
    public static class CodeActionKind
    {
        public const string Empty = "";
        public const string QuickFix = "quickfix";

        public static class Refactor
        {
            public const string Generic = "refactor";
            public const string Extract = "refactor.extract";
            public const string Inline = "refactor.inline";
            public const string Rewrite = "refactor.rewrite";
        }

        public static class Source
        {
            public const string Generic = "source";
            public const string OrganizeImports = "source.organizeImports";
            public const string FixAll = "source.fixAll";
        }
    }
}