namespace Microsoft.PowerPlatformLS.Contracts.Internal
{
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using System.Collections.Immutable;

    public static class WorkspacePath
    {
        private static readonly ImmutableArray<(string, LanguageType)> ExtensionToLanguageTypes = [
            (".mcs.yaml", LanguageType.CopilotStudio),
            (".mcs.yml", LanguageType.CopilotStudio),
            ("botdefinition.json", LanguageType.CopilotStudio),
            (".fx1", LanguageType.PowerFx),
            (".yml", LanguageType.Yaml),
            (".yaml", LanguageType.Yaml),
            ("icon.png", LanguageType.CopilotStudio),
        ];

        public static bool TryGetLanguageType(FilePath path, out LanguageType languageType)
        {
            var extension = GetExtension(path);
            if (extension != null)
            {
                foreach (var (ext, type) in ExtensionToLanguageTypes)
                {
                    if (extension.Equals(ext, StringComparison.OrdinalIgnoreCase))
                    {
                        languageType = type;
                        return true;
                    }
                }
            }

            languageType = default;
            return false;
        }


        public static FilePath RemoveExtension(FilePath path)
        {
            var extension = GetExtension(path);
            if (extension != null)
            {
                var pathValue = path.ToString();
                int newLength = pathValue.Length - extension.Length;
                if (newLength > 0)
                {
                    return new FilePath(pathValue.Substring(0, newLength));
                }
            }

            return path;
        }

        public static string? GetExtension(FilePath path)
        {
            var pathValue = path.ToString();
            foreach (var (extension, languageType) in ExtensionToLanguageTypes)
            {
                if (pathValue.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                {
                    return extension;
                }
            }

            return Path.GetExtension(pathValue);

        }
    }
}