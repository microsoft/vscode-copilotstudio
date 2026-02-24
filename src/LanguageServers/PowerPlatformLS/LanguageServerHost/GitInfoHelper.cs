namespace Microsoft.PowerPlatformLS.LanguageServerHost
{
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using System.Text.Json;
    using System.Text.Json.Serialization;

    internal static class GitInfoHelper
    {
        private static string? ReadGitInfo()
        {
            using var stream = typeof(GitInfoHelper).Assembly
                .GetManifestResourceStream(typeof(GitInfoHelper).Namespace + ".gitinfo.txt");

            if (stream == null)
            {
                return null;
            }

            using var sr = new StreamReader(stream);
            return sr.ReadToEnd();
        }

        public static string GetGitHash()
        {
            var str = ReadGitInfo();
            if (str != null)
            {
                // expected file was created by:
                // git.exe log -1 --format="hash:%H%nauthor:%an%ntitle:%s%n"
                //
                // git log doesn't escape, so it can't reliably produce json. 
                string? hash = null;

                var lines = str.Split('\n');
                foreach (var line in lines)
                {
                    if (string.IsNullOrEmpty(line) || line.StartsWith("//"))
                    {
                        continue;
                    }

                    // tag:value 
                    int i = line.IndexOf(':');
                    if (i < 0)
                    {
                        continue;
                    }
                    string tag = line.Substring(0, i).Trim();
                    string value = line.Substring(i + 1).Trim();

                    if (tag == "hash")
                    {
                        hash = value.Trim();
                    }
                }

                return hash ?? string.Empty;
            }

            return string.Empty;
        }

        // Describe Package.json elements that we want to read. 
        private class PackageJson
        {
            [JsonPropertyName("version")]
            public string? Version { get; set; }
        }

        // Build process dynmically updates package.json version, and this becomes the vsix version.
        public static string GetVsixVersion()
        {
            var json = GetPackageJson();

            var obj = JsonSerializer.Deserialize<PackageJson>(json);

            return obj?.Version ?? "???";
        }
                
        public static string GetPackageJson()
        {
            using var stream = typeof(GitInfoHelper).Assembly
                .GetManifestResourceStream(typeof(GitInfoHelper).Namespace + ".package.json");

            if (stream != null)
            {
                using var sr = new StreamReader(stream);
                return sr.ReadToEnd();
            }
            return "{}";
        }

        // Get dependency information
        public static void AddVersionInfo(this IServiceCollection services)
        {
            var versionInfo = new BuildVersionInfo
            {
                Hash = GetGitHash(),
                VsixVersion = GetVsixVersion()
            };

            services.AddSingleton(versionInfo);
        }
    }
}
