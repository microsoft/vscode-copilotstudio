namespace Microsoft.PowerPlatformLS.Contracts.Internal.Common
{
    using System.Diagnostics;
    using System.Diagnostics.CodeAnalysis;

    /// <summary>
    /// Arbitrary file path.
    /// Can be either an absolute path or a relative path to a parent directory.
    /// Use / convention.
    /// </summary>
    [DebuggerDisplay("{_value}")]
    public readonly struct FilePath
    {
        private readonly string _value;

        /// <summary>
        /// Initializes a new instance of the <see cref="FilePath"/> class.
        /// </summary>
        /// <param name="path">The file path string.</param>
        /// <exception cref="ArgumentException">Thrown if the file path is empty or root.</exception>
        public FilePath(string path)
        {
            if (path.Length == 0 || path == "/")
            {
                throw new ArgumentException($"File path cannot be empty or root. Use '{nameof(DirectoryPath)}'.", nameof(path));
            }
            PathHelper.ValidatePath(path);
            _value = path;
            ParentDirectoryPath = GetParentDirectoryPath();
        }

        /// <summary>
        /// Gets the parent directory path of this file.
        /// </summary>
        /// <returns>The parent <see cref="DirectoryPath"/>.</returns>
        public DirectoryPath ParentDirectoryPath { get; }

        /// <summary>
        /// Gets the file name without its extension.
        /// </summary>
        public string FileNameWithoutExtension => WorkspacePath.RemoveExtension(this).FileName;

        /// <summary>
        /// Gets the file name with its extension.
        /// </summary>
        public string FileName => Path.GetFileName(ToString());

        /// <summary>
        /// Returns the path relative to the given parent.
        /// Assumes that the parent is a valid parent of this path.
        /// Throws otherwise.
        /// Client can call <see cref="DirectoryPath.Contains(FilePath)"/> if needed.
        /// </summary>
        /// <param name="parent">The parent directory path.</param>
        /// <returns>The relative path as <see cref="FilePath"/>.</returns>
        public FilePath GetRelativeTo(DirectoryPath parent)
        {
            return PathHelper.GetRelativeTo(this, parent, GetValue, x => new FilePath(x));
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

        private DirectoryPath GetParentDirectoryPath()
        {
            var lastSlashIndex = _value.LastIndexOf('/');
            if (lastSlashIndex < 0)
            {
                // this could happen for relative paths like "file.txt"
                return new DirectoryPath(string.Empty);
            }

            return new DirectoryPath(_value.Substring(0, lastSlashIndex + 1));
        }

        private static string GetValue(FilePath path) => path._value;
    }
}