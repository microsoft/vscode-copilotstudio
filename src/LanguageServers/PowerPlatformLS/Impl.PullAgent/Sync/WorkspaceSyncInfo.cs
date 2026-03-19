namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.Agents.ObjectModel;

    internal class WorkspaceSyncInfo
    {
        public required DefinitionBase Definition { get; init; }

        public required PvaComponentChangeSet Changeset { get; init; }
    }
}
