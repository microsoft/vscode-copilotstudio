

namespace Microsoft.PowerPlatformLS.Impl.Language.PowerFx
{
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models.Lsp;
    using System.Globalization;

    internal class PowerFxLanguage : ILanguageAbstraction
    {
        LanguageType ILanguageAbstraction.LanguageType { get; } = LanguageType.PowerFx;

        LspDocument ILanguageAbstraction.CreateDocument(FilePath path, string text, CultureInfo culture, DirectoryPath workspaceFolderPath) => new PowerFxLspDocument(path, text, culture, workspaceFolderPath);

        bool ILanguageAbstraction.IsValidAgentDirectory(DirectoryPath documentDirectory, out DirectoryPath validDirectory)
        {
            validDirectory = documentDirectory;
            return false;
        }
    }
}