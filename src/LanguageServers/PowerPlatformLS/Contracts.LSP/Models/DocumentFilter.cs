namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Models
{
    /// <summary>
    /// https://microsoft.github.io/language-server-protocol/specifications/lsp/3.17/specification/#documentFilter
    /// A document filter denotes a document through properties like language, scheme or pattern. An example is a filter that
    /// applies to TypeScript files on disk. Another example is a filter that applies to JSON files with name package.json:
    /// { language: 'typescript', scheme: 'file' }
    /// { language: 'json', pattern: '**/package.json' }
    /// </summary>
    public sealed class DocumentFilter
    {
        public string? Language { get; set; }

        public string? Scheme { get; set; }
    }
}