namespace Microsoft.PowerPlatformLS.Contracts.FileLayout
{
    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using System.Diagnostics.CodeAnalysis;

    /// <summary>
    /// Path of a file inherently relative to an agent directory.
    /// </summary>
    public readonly struct AgentFilePath
    {
        private static readonly DirectoryPath AgentsDirectory = new DirectoryPath("agents");
        private readonly FilePath _value;

        public AgentFilePath(string value) : this(new FilePath(value))
        {
        }

        public AgentFilePath(FilePath path)
        {
            var pathValue = path.ToString();
            if (Path.IsPathRooted(pathValue))
            {
                throw new ArgumentException("AgentFilePath must be relative to an agent directory.", nameof(path));
            }

            if (pathValue.StartsWith("../") ||
                pathValue.StartsWith("./") ||
                pathValue.Contains("/../") ||
                pathValue.Contains("/./") ||
                pathValue.EndsWith("/.") ||
                pathValue.EndsWith("/.."))
            {
                throw new ArgumentException("AgentFilePath must be fully resolved. It must be a canonical relative path. It cannot refer to current or parent directory.", nameof(path));
            }

            _value = path;
        }

        public string FileNameWithoutExtension => _value.FileNameWithoutExtension;


        // Subagents are in a subdir: /agents/subName/topics/mytopic.mcs/yml
        // Return false if not a sub agent directory. 
        // Return true:
        //    agentName = subName
        //    subPath = topics/mytopic.mcs/yml
        public bool TryGetSubAgentName(
            [NotNullWhen(true)] out string? agentName,
            [NotNullWhen(true)] out AgentFilePath? subPath)
        {
            if (AgentsDirectory.Contains(_value))
            {
                int start = AgentsDirectory.Length;
                var pathValue = _value.ToString();
                int end = pathValue.IndexOf('/', start);

                // File is directly in agents/ with no {agentName}/ subdirectory
                if (end < 0)
                {
                    agentName = null;
                    subPath = null;
                    return false;
                }

                int len = end - start;

                agentName = pathValue.Substring(start, len);

                string x = pathValue.Substring(end + 1);
                subPath = new AgentFilePath(x);
                return true;
            }

            agentName = null;
            subPath = null;
            return false;
        }

        public bool IsDefinition() => IsDefinition(_value);

        public static bool IsDefinition(FilePath filePath)
        {
            return filePath.ToString().EndsWith("botdefinition.json", StringComparison.OrdinalIgnoreCase);
        }

        public AgentFilePath RemoveExtension() => new AgentFilePath(WorkspacePath.RemoveExtension(_value));

        /// <summary>
        /// Gets a value indicating whether this file is in a subdirectory.
        /// </summary>
        public bool IsInSubDir => ToString().IndexOf('/') >= 0;

        public string ParentDirectoryName => _value.ParentDirectoryPath.ToString();

        public string FileName => _value.FileName;

        public bool IsIcon() => IsIcon(_value);

        public static bool IsIcon(FilePath fileName)
        {
            return string.Equals(fileName.FileName, "icon.png", StringComparison.OrdinalIgnoreCase);
        }

        public override string ToString()
        {
            return _value.ToString();
        }

        public override bool Equals([NotNullWhen(true)] object? obj)
        {
            if (obj is AgentFilePath other)
            {
                return _value.Equals(other._value);
            }

            return false;
        }

        public override int GetHashCode()
        {
            return _value.GetHashCode();
        }
    }
}
