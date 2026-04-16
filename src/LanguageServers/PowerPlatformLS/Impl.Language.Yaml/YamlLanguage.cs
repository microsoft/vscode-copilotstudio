namespace Microsoft.PowerPlatformLS.Impl.Language.Yaml
{
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models.Lsp;
    using System.Globalization;

    internal class YamlLanguage : ILanguageAbstraction
    {
        LanguageType ILanguageAbstraction.LanguageType => LanguageType.Yaml;

        LspDocument ILanguageAbstraction.CreateDocument(FilePath path, string text, CultureInfo culture, DirectoryPath workspaceFolderPath) => new YamlLspDocument(path, text, workspaceFolderPath);

        bool ILanguageAbstraction.IsValidAgentDirectory(DirectoryPath documentDirectory, out DirectoryPath validDirectory)
        {
            validDirectory = documentDirectory;
            return false;
        }
    }
}