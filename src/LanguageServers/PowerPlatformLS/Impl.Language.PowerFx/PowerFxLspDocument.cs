namespace Microsoft.PowerPlatformLS.Impl.Language.PowerFx
{

    using Microsoft.PowerFx;
    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models.Lsp;

    internal class PowerFxLspDocument : LspDocument<CheckResult>
    {
        private readonly System.Globalization.CultureInfo _culture;
        public PowerFxLspDocument(FilePath path, string text, System.Globalization.CultureInfo culture, DirectoryPath workspacePath)
            : base(path, text, Constants.LanguageIds.PowerFx, workspacePath)
        {
            _culture = culture;
        }

        protected override CheckResult? ComputeModel()
        {
            var checkResult = new RecalcEngine().Check(Text, new ParserOptions { Culture = _culture });
            checkResult.ApplyBinding();
            return checkResult;
        }
    }
}