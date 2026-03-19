namespace Microsoft.PowerPlatformLS.Contracts.Internal.Common
{
    using System.Diagnostics;
    using System.Diagnostics.CodeAnalysis;

    /// <summary>
    /// Agent directory path.
    /// Can be either an absolute path or a relative path to a parent agent directory.
    /// Use / convention and always ends with a slash, except for the root directory which is represented by an empty string.
    /// </summary>
    /// <example>
    /// All possible cases are listed here.
    /// Absolute agent directory (common case):  "c:/my/agent/directory/"
    /// Absolute sub-agent directory:            "c:/my/agent/directory/agents/subagent1/"
    /// Relative sub-agent directory:            "subagent1/"
    /// Root:                                    ""
    /// </example>
    [DebuggerDisplay("{_value}")]
    public readonly struct DirectoryPath
    {
        private readonly string _value;

        /// <summary>
        /// Initializes a new instance of the <see cref="DirectoryPath"/> class.
        /// </summary>
        /// <param name="path">The directory path string.</param>
        public DirectoryPath(string path)
        {
            _value = path.Length == 0 || (path[^1] == '/') ? path : path + "/";
            PathHelper.ValidatePath(_value);
        }

        public int Length => _value.Length;

        /// <summary>
        /// Returns the path relative to the given parent.
        /// Assumes that the parent is a valid parent of this path.
        /// Throws otherwise.
        /// Use <see cref="GetRelativeFrom"/> if you need to support peers. 
        /// Client can call <see cref="DirectoryPath.Contains(DirectoryPath)"/> if needed.
        /// </summary>
        /// <param name="parent">The parent directory path.</param>
        /// <returns>The relative path as <see cref="DirectoryPath"/>.</returns>
        public DirectoryPath GetRelativeTo(DirectoryPath parent)
        {
            return PathHelper.GetRelativeTo(this, parent, GetValue, x => new DirectoryPath(x));
        }

        /// <summary>
        /// Determines whether the specified child path is contained within this directory path.
        /// </summary>
        /// <param name="childPath">The child path to check.</param>
        /// <returns>true if the child path is contained; otherwise, false.</returns>
        public bool Contains(DirectoryPath childPath)
        {
            return Contains<DirectoryPath>(childPath);
        }

        /// <summary>
        /// Determines whether the specified child path is contained within this directory path.
        /// </summary>
        /// <param name="childPath">The child path to check.</param>
        /// <returns>true if the child path is contained; otherwise, false.</returns>
        public bool Contains(FilePath childPath)
        {
            return Contains<FilePath>(childPath);
        }

        private bool Contains<T>(T childPath) where T : struct
        {
            if (_value.Length == 0)
            {
                return true;
            }

            var childPathValue = childPath.ToString();
            if (childPathValue == null || childPathValue.Length == 0)
            {
                return false;
            }

            return childPathValue.StartsWith(_value, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Gets a <see cref="FilePath"/> representing a file in this directory.
        /// </summary>
        /// <param name="child">The file name.</param>
        /// <returns>A <see cref="FilePath"/> for the child file.</returns>
        public FilePath GetChildFilePath(string child)
        {
            return new FilePath(_value + child);
        }

        /// <summary>
        /// Gets a <see cref="DirectoryPath"/> representing a subdirectory in this directory.
        /// </summary>
        /// <param name="child">The subdirectory name.</param>
        /// <returns>A <see cref="DirectoryPath"/> for the child directory.</returns>
        public DirectoryPath GetChildDirectoryPath(string child)
        {
            return new DirectoryPath(_value + child);
        }

        /// <summary>
        /// Gets the parent directory path of this directory.
        /// </summary>
        /// <returns>The parent <see cref="DirectoryPath"/>.</returns>
        public DirectoryPath GetParentDirectoryPath()
        {
            if (_value.Length < 2)
            {
                // Root directory has no parent.
                return this;
            }

            var secondLastSlashIndex = _value.LastIndexOf('/', _value.Length - 2);
            if (secondLastSlashIndex < 0)
            {
                // this could happen for relative paths like "subagent1/"
                return new DirectoryPath(string.Empty);
            }

            return new DirectoryPath(_value.Substring(0, secondLastSlashIndex + 1));
        }

        /// <summary>
        /// Returns the string representation of the path.
        /// </summary>
        /// <returns>The path as a string.</returns>
        public override string ToString()
        {
            return PathHelper.GetString(this, GetValue);
        }

        /// <summary>
        /// Determines whether the specified object is equal to the current object.
        /// </summary>
        /// <param name="obj">The object to compare with the current object.</param>
        /// <returns>true if the objects are equal; otherwise, false.</returns>
        public override bool Equals([NotNullWhen(true)] object? obj)
        {
            return PathHelper.AreEqual(this, obj, GetValue);
        }

        /// <summary>
        /// Returns a hash code for the current object.
        /// </summary>
        /// <returns>A hash code for the current object.</returns>
        public override int GetHashCode()
        {
            return PathHelper.GetHashCode(this, GetValue);
        }

        private static string GetValue(DirectoryPath path) => path._value;

        /// <summary>
        /// Throw if not rooted.
        /// </summary>
        /// <exception cref="InvalidOperationException"></exception>
        public void EnsureIsRooted()
        {
            if (!Path.IsPathRooted(_value))
            {
                throw new InvalidOperationException($"Paths musted be rooted: {_value}");
            }
        }

        /// <summary>
        /// Compute a relative path between 2 peer directories.
        /// This is useful for resolve references between workspaces. 
        /// </summary>
        /// <param name="parent"></param>
        /// <returns></returns>
        /// <exception cref="InvalidOperationException"></exception>
        public RelativeDirectoryPath GetRelativeFrom(DirectoryPath parent)
        {
            EnsureIsRooted();
            parent.EnsureIsRooted();

            var relative = Path.GetRelativePath(parent._value, _value);
            relative = relative.Replace('\\', '/');

            return new RelativeDirectoryPath(relative);
        }

        // Useful for resolving relative links in files. 
        public DirectoryPath ResolveRelativeRef(RelativeDirectoryPath relative)
        {
            string path = relative.ToString();

            var final = this;

            while (path.StartsWith("../"))
            {
                final = final.GetParentDirectoryPath();
                path = path.Substring(3);
            }

            final = final.GetChildDirectoryPath(path);
            return final;
        }
    }
}