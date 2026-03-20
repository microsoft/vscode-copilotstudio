namespace Microsoft.PowerPlatformLS.Contracts.Internal.Common
{
    using System.Diagnostics;

    /// <summary>
    /// Represent a relative directory reference that could start with "../" and refer to peer directories.
    /// This may get serialized in yml files when describing references between workspaces. 
    /// The only operation we should do here is resolve to a <see cref="DirectoryPath"/> before usage. 
    /// </summary>
    [DebuggerDisplay("{_value}")]
    public readonly struct RelativeDirectoryPath
    {
        private readonly string _value;

        public RelativeDirectoryPath(string value)
        {
            _value = value;
            PathHelper.ValidatePath(_value);
        }

        public override string ToString()
        {
            return _value;
        }
    }
}
