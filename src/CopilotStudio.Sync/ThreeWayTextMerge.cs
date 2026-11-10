// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Text;
using Microsoft.Agents.ObjectModel.Merge;

namespace Microsoft.CopilotStudio.Sync;

internal static class ThreeWayTextMerge
{
    internal static string Merge(string? original, string? local, string? remote) => TextEquivalence.InLineEndingsOf(Merge(InLfForm(original), InLfForm(local), InLfForm(remote), refuseConflicts: false, keepLocalOnConflict: false, "\n", out _) ?? string.Empty, local);

    internal static string? Merge(string? original, string? local, string? remote, WorkingCopyConflictMode mode, out bool conflicted) => Merge(original, local, remote, mode == WorkingCopyConflictMode.Report, mode == WorkingCopyConflictMode.Ours, "\n", out conflicted);

    private static string? Merge(string? original, string? local, string? remote, bool refuseConflicts, bool keepLocalOnConflict, string newLine, out bool conflicted)
    {
        var originalFile = Read(original);
        var localFile = Read(local);
        var remoteFile = Read(remote);
        var mergeOptions = new MergeOptions();
        var changes = MergeFinder.Merge(originalFile, localFile, remoteFile, new DiffLineComparer(mergeOptions), mergeOptions);
        conflicted = changes.Any(change => change.ChangeType == MergeChangeType.Conflict);
        if (refuseConflicts && conflicted)
        {
            return null;
        }

        using var writer = new StringWriter { NewLine = newLine };
        new MergeOutput(mergeOptions, writer).Output(originalFile, localFile, remoteFile, keepLocalOnConflict ? changes.Select(change => change.ChangeType == MergeChangeType.Conflict ? new OursChange(change) : change).ToArray() : changes);
        return writer.ToString();
    }

    private static DiffFile Read(string? text) => DiffFile.Create(new StringReader(text ?? string.Empty), Encoding.UTF8, new DiffOptions());

    private static string? InLfForm(string? text) => text == null ? null : TextEquivalence.Checkout.Normalize(text);

    private sealed class OursChange : IMergeChange
    {
        private readonly IMergeChange _conflict;

        public OursChange(IMergeChange conflict)
        {
            _conflict = conflict;
        }

        public MergeChangeType ChangeType => MergeChangeType.Modified;

        public IDiffChange[] ModifiedChanges => _conflict.ModifiedChanges;

        public IDiffChange[] LatestChanges => _conflict.LatestChanges;

        public IDiffChange ModifiedChange => _conflict.ModifiedChange;

        public IDiffChange LatestChange => _conflict.LatestChange;
    }
}
