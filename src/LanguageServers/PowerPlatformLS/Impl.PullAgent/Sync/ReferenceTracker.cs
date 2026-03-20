namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;

    // Provide filepaths to resolve a cross-workspace reference. 
    // Map from Schema Ids (which are used in the source files) to the workspace that defines them.
    // This should be a short-lived object just for hte lifetime of a clone operation.
    // It should not be persisted because the real source of truth is already on the filesystem.
    internal class ReferenceTracker
    {
        private readonly Dictionary<BotComponentCollectionSchemaName, DirectoryPath> _paths = new Dictionary<BotComponentCollectionSchemaName, DirectoryPath>();

        // Mark where this is declared.
        // This (relative) path will get written into the reference.mcs.yml source files.
        public void MarkDeclaration(BotComponentCollectionSchemaName id, DirectoryPath path)
        {
            path.EnsureIsRooted();

            _paths.Add(id, path);
        }

        // Fetch from previous Mark.
        // This could fail if we refer to something that we can't resolve.
        // (perhaps it was never cloned to disk).
        public bool TryGetComponentCollection(
            BotComponentCollectionSchemaName id,
            out DirectoryPath path)
        {
            return _paths.TryGetValue(id, out path);
        }
    }
}
