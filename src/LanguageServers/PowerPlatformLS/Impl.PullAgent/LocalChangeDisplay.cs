namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.CopilotStudio.Sync;
    using Microsoft.PowerPlatformLS.Contracts.FileLayout;
    using System;
    using System.Collections.Immutable;
    using System.Linq;

    internal static class LocalChangeDisplay
    {
        internal static ImmutableArray<Change> ForWorkspace(IMcsWorkspace workspace, ImmutableArray<Change> changes)
        {
            if (!changes.Any(change => change.ChangeType == ChangeType.Delete))
            {
                return changes;
            }

            var unreadablePaths = workspace.GetUnreadableDocuments()
                .Select(document => document.FilePath.ToString())
                .ToHashSet(StringComparer.Ordinal);
            if (unreadablePaths.Count == 0)
            {
                return changes;
            }

            return changes.Select(change =>
                change.ChangeType == ChangeType.Delete && unreadablePaths.Contains(change.Uri.Replace('\\', '/'))
                    ? change with { ChangeType = ChangeType.Update }
                    : change).ToImmutableArray();
        }
    }
}
