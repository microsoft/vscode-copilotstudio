// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.CopilotStudio.Sync;

/// <summary>How a working-copy merge settles a path that both sides changed in ways that collide.</summary>
public enum WorkingCopyConflictMode
{
    /// <summary>Reports each colliding path as conflicted without producing its content, as a capture does.</summary>
    Report = 0,

    /// <summary>Keeps the local side of every collision while the other changes of both sides still merge, as a capture that resolves conflicts to the cloud does.</summary>
    Ours = 1,

    /// <summary>Writes both sides of each collision between conflict markers.</summary>
    Markers = 2,
}

/// <summary>What a working-copy merge does to one path of the local files.</summary>
public enum WorkingCopyMergeOutcome
{
    /// <summary>The local file stays as it is, or stays absent.</summary>
    Unchanged = 0,

    /// <summary>The content is written where the local files hold no file.</summary>
    Added = 1,

    /// <summary>The incoming file replaces the local file.</summary>
    Updated = 2,

    /// <summary>The local file is deleted.</summary>
    Deleted = 3,

    /// <summary>The merge of both sides' changes replaces the local file.</summary>
    Merged = 4,

    /// <summary>Both sides changed the file in ways that collide.</summary>
    Conflicted = 5,

    /// <summary>The incoming file replaces a file the text merge can't settle.</summary>
    Replaced = 6,
}

/// <summary>What a working-copy merge does to one path.</summary>
/// <param name="Path">The '/'-separated relative path.</param>
/// <param name="Outcome">What happens to the local file.</param>
/// <param name="Content">The content to write, or <see langword="null"/> when nothing is written.</param>
public sealed record WorkingCopyMergeEntry(string Path, WorkingCopyMergeOutcome Outcome, ReadOnlyMemory<byte>? Content);

/// <summary>What a working-copy merge does to each path, and the local files it leaves.</summary>
/// <param name="Entries">One entry for each path any of the three file maps holds, in ordinal path order.</param>
/// <param name="Files">The local files with the content of every entry written and every deletion applied, keyed by ordinal path.</param>
public sealed record WorkingCopyMergeResult(IReadOnlyList<WorkingCopyMergeEntry> Entries, IReadOnlyDictionary<string, ReadOnlyMemory<byte>> Files);

/// <summary>Merges the changes from a base to an incoming set of files into the local files, file by file and with the three-way merge Sync's own pull uses.</summary>
public static class WorkingCopyMerge
{
    /// <summary>Merges the changes from the base files to the incoming files into the local files, each map keyed by '/'-separated relative path, without reading or writing anything.</summary>
    /// <param name="baseFiles">The files both sides started from.</param>
    /// <param name="localFiles">The local files, whose side the merge keeps in <see cref="WorkingCopyConflictMode.Ours"/> mode.</param>
    /// <param name="incomingFiles">The files whose changes since the base are merged in.</param>
    /// <param name="mode">How paths both sides changed in ways that collide are settled.</param>
    /// <returns>What happens to each path, and the local files afterwards.</returns>
    public static WorkingCopyMergeResult Merge(IReadOnlyDictionary<string, ReadOnlyMemory<byte>> baseFiles, IReadOnlyDictionary<string, ReadOnlyMemory<byte>> localFiles, IReadOnlyDictionary<string, ReadOnlyMemory<byte>> incomingFiles, WorkingCopyConflictMode mode)
    {
        if (mode is not (WorkingCopyConflictMode.Report or WorkingCopyConflictMode.Ours or WorkingCopyConflictMode.Markers))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "The conflict mode is not one Sync knows.");
        }

        var entries = baseFiles.Keys.Concat(localFiles.Keys).Concat(incomingFiles.Keys).Distinct(StringComparer.Ordinal).OrderBy(path => path, StringComparer.Ordinal).Select(path => MergeFile(path, FileVersion.Find(baseFiles, path), FileVersion.Find(localFiles, path), FileVersion.Find(incomingFiles, path), mode)).ToList();
        var files = localFiles.ToDictionary(file => file.Key, file => file.Value, StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry.Outcome == WorkingCopyMergeOutcome.Deleted)
            {
                files.Remove(entry.Path);
            }
            else if (entry.Content is { } content)
            {
                files[entry.Path] = content;
            }
        }

        return new WorkingCopyMergeResult(entries, files);
    }

    private static WorkingCopyMergeEntry MergeFile(string path, FileVersion? baseVersion, FileVersion? localVersion, FileVersion? incomingVersion, WorkingCopyConflictMode mode)
    {
        if (FileVersion.Same(baseVersion, incomingVersion))
        {
            return new WorkingCopyMergeEntry(path, WorkingCopyMergeOutcome.Unchanged, null);
        }

        if (FileVersion.Same(localVersion, baseVersion))
        {
            return new WorkingCopyMergeEntry(path, incomingVersion is null ? WorkingCopyMergeOutcome.Deleted : localVersion is null ? WorkingCopyMergeOutcome.Added : WorkingCopyMergeOutcome.Updated, incomingVersion?.Content);
        }

        if (FileVersion.Same(localVersion, incomingVersion))
        {
            return new WorkingCopyMergeEntry(path, WorkingCopyMergeOutcome.Unchanged, null);
        }

        if (localVersion?.Text is not { } localText || incomingVersion?.Text is not { } incomingText || baseVersion is { Text: null })
        {
            return mode == WorkingCopyConflictMode.Markers ? SettleWithoutTextMerge(path, localVersion, incomingVersion) : Unsettled(path, mode);
        }

        var mergedText = ThreeWayTextMerge.Merge(baseVersion?.Text?.Normalized, localText.Normalized, incomingText.Normalized, mode, out var conflicted);

        return mergedText is null ? Unsettled(path, mode) : string.Equals(mergedText, localText.Normalized, StringComparison.Ordinal) ? new WorkingCopyMergeEntry(path, WorkingCopyMergeOutcome.Unchanged, null) : new WorkingCopyMergeEntry(path, conflicted && mode == WorkingCopyConflictMode.Markers ? WorkingCopyMergeOutcome.Conflicted : WorkingCopyMergeOutcome.Merged, Encode(mergedText, localText.Original));
    }

    private static WorkingCopyMergeEntry Unsettled(string path, WorkingCopyConflictMode mode) => new(path, mode == WorkingCopyConflictMode.Report ? WorkingCopyMergeOutcome.Conflicted : WorkingCopyMergeOutcome.Unchanged, null);

    private static WorkingCopyMergeEntry SettleWithoutTextMerge(string path, FileVersion? localVersion, FileVersion? incomingVersion) => localVersion is null || incomingVersion is null ? new(path, WorkingCopyMergeOutcome.Conflicted, incomingVersion?.Content) : new(path, WorkingCopyMergeOutcome.Replaced, incomingVersion.Content);

    private static ReadOnlyMemory<byte> Encode(string mergedText, string localText)
    {
        var keepsFinalLineEnd = localText.Length == 0 || localText.EndsWith("\n", StringComparison.Ordinal) || localText.EndsWith("\r", StringComparison.Ordinal) || !mergedText.EndsWith("\n", StringComparison.Ordinal);
        var text = keepsFinalLineEnd ? mergedText : mergedText.Substring(0, mergedText.Length - 1);
        return TextEquivalence.Merge.Encoding.GetBytes(TextEquivalence.InLineEndingsOf(text, localText));
    }

    private sealed record TextContent(string Original, string Normalized);

    private sealed class FileVersion
    {
        private readonly Lazy<TextContent?> _text;

        private FileVersion(ReadOnlyMemory<byte> content)
        {
            Content = content;
            _text = new Lazy<TextContent?>(() => TextEquivalence.Merge.Decode(content) is { } text ? new TextContent(text, TextEquivalence.Merge.Normalize(text)) : null);
        }

        public ReadOnlyMemory<byte> Content { get; }

        public TextContent? Text => _text.Value;

        public static FileVersion? Find(IReadOnlyDictionary<string, ReadOnlyMemory<byte>> files, string path) => files.TryGetValue(path, out var content) ? new FileVersion(content) : null;

        public static bool Same(FileVersion? first, FileVersion? second) => first is null || second is null ? first is null && second is null : first.Content.Span.SequenceEqual(second.Content.Span) || (first.Text is { } firstText && second.Text is { } secondText && string.Equals(firstText.Normalized, secondText.Normalized, StringComparison.Ordinal));
    }
}
