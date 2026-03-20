
namespace Microsoft.PowerPlatformLS.Contracts.FileLayout
{
    using System.Diagnostics.CodeAnalysis;

    /// <summary>
    /// Allow to compare file path without their extensions.
    /// </summary>
    public class AgentFilePathWithoutExtensionComparer : IEqualityComparer<AgentFilePath>
    {
        public bool Equals(AgentFilePath xPath, AgentFilePath yPath)
        {
            if (xPath.Equals(yPath))
            {
                return true;
            }

            xPath = xPath.RemoveExtension();
            yPath = yPath.RemoveExtension();

            return xPath.Equals(yPath);
        }

        public int GetHashCode([DisallowNull] AgentFilePath obj)
        {
            obj = obj.RemoveExtension();
            return obj.GetHashCode();
        }
    }
}
