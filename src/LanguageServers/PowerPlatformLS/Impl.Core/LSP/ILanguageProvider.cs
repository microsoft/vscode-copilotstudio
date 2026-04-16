namespace Microsoft.PowerPlatformLS.Impl.Core.Lsp
{
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using Microsoft.PowerPlatformLS.Impl.Core.Lsp.Uris;
    using System.Diagnostics.CodeAnalysis;

    internal interface ILanguageProvider
    {
        /// <summary>
        /// Resolve the language for the given document URI.
        /// </summary>
        /// <param name="uri">Document URI (typed).</param>
        /// <param name="language"></param>
        /// <returns>A language abstraction matching the file language. Null if no language is defined for the given file uri.</returns>
        bool TryGetLanguageForDocument(LspUri uri, [NotNullWhen(true)] out ILanguageAbstraction? language);

        /// <summary>
        /// Get the language given its identifier.
        /// </summary>
        public bool TryGetLanguage(LanguageType languageType, [NotNullWhen(true)] out ILanguageAbstraction? language);
    }
}