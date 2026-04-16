namespace Microsoft.PowerPlatformLS.Impl.Core.Lsp
{
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using Microsoft.PowerPlatformLS.Impl.Core.Lsp.Uris;
    using System.Diagnostics.CodeAnalysis;

    internal class LspLanguageProvider : ILanguageProvider
    {
        private readonly ILspServices _lspServices;
        private readonly IDictionary<LanguageType, ILanguageAbstraction> _languages;

        public LspLanguageProvider(IEnumerable<ILanguageAbstraction> languages, ILspServices lspServices)
        {
            _lspServices = lspServices;
            _languages = languages.ToDictionary(analyzer => analyzer.LanguageType, analyzer => analyzer);

            if (_languages.Count == 0)
            {
                lspServices.GetRequiredService<ILspLogger>().LogWarning("No language registered. Editor features will be limited to default language.");
            }

            if (_languages.Count != languages.Count())
            {
                var duplicateTypesMessage = string.Join(", ", languages
                    .GroupBy(analyzer => analyzer.LanguageType)
                    .Where(group => group.Count() > 1)
                    .Select(group => $"{group.Key} ({string.Join(',', group.Select(x => x.GetType().Name))})"));

                throw new ArgumentException($"Duplicate language found for the following {nameof(LanguageType)}(s): {duplicateTypesMessage}", nameof(languages));
            }
        }

        /// <inheritdoc />
        public bool TryGetLanguageForDocument(LspUri uri, [NotNullWhen(true)] out ILanguageAbstraction? language)
        {
            // Phase 1b: Pattern match to only handle file URIs, bridge to existing logic
            if (uri is FileLspUri fileLspUri && WorkspacePath.TryGetLanguageType(fileLspUri.AsFilePathNormalized(), out var languageType))
            {
                if (!_languages.TryGetValue(languageType, out language))
                {
                    throw new KeyNotFoundException($"No analyzer defined for the '{languageType}' type.");
                }

                return true;
            }

            language = null;
            return false;
        }

        bool ILanguageProvider.TryGetLanguage(LanguageType languageType, [NotNullWhen(true)] out ILanguageAbstraction? language)
        {
            return _languages.TryGetValue(languageType, out language);
        }
    }
}