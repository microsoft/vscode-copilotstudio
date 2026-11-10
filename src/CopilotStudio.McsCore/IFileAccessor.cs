// Copyright (C) Microsoft Corporation. All rights reserved.
// Ported from om/src/vscode/LanguageServers/PowerPlatformLS/Impl.PullAgent/File/IFileAccessor.cs

using System.Text;
using System.Threading;

namespace Microsoft.CopilotStudio.McsCore;

/// <summary>
/// Abstraction over file access.
/// Note that .NET IFileProvider is a read-only abstraction, and this specifically needs writes.
/// </summary>
public interface IFileAccessor
{
    bool Exists(AgentFilePath path);

    void CreateHiddenDirectory(AgentFilePath path);

    /// <summary>
    /// Write contents to disk.
    /// Overwrite file. Create directory if needed.
    /// </summary>
    Stream OpenWrite(AgentFilePath path);

    /// <summary>
    /// Return a read-only stream for accessing the file.
    /// </summary>
    /// <exception cref="FileNotFoundException">Thrown if directory or file does not exist.</exception>
    Stream OpenRead(AgentFilePath path);

    /// <summary>
    /// Delete the file.
    /// nop if file doesn't exist.
    /// </summary>
    void Delete(AgentFilePath path);

    /// <summary>
    /// Recursively delete the directory and all of its contents.
    /// nop if the directory doesn't exist.
    /// </summary>
    void DeleteDirectory(AgentFilePath path);

    /// <summary>
    /// Replace file content from sourcePath to targetPath.
    /// </summary>
    void Replace(AgentFilePath sourcePath, AgentFilePath targetPath);

    /// <summary>
    /// List files in the directory.
    /// </summary>
    /// <param name="relativeFolder">The relative folder path to search within. If null, the root directory is used.</param>
    /// <param name="filePattern">Search pattern is supported, e.g. "*.mcs.yml"</param>
    /// <returns>An enumerable of <see cref="AgentFilePath"/> representing the files that match the search pattern.</returns>
    IEnumerable<AgentFilePath> ListFiles(string? relativeFolder = null, string filePattern = "*.*");
}

internal static class FileAccessorExtensions
{
    private static readonly byte[] ByteOrderMark = Encoding.UTF8.GetPreamble();

    // No #if for the WriteAsync/ReadStringAsync overloads below: the
    // netstandard2.0-compatible call shapes produce identical behavior on net10.
    //   - WriteAsync(string) avoids the contents.AsMemory() detour. We already hold
    //     a string; the ROM<char> overload offers no allocation savings here.
    //   - Stream.WriteAsync(byte[], 0, len, CT) is the underlying overload that
    //     net10's WriteAsync(byte[], CT) wraps. Pass the offset/length explicitly
    //     so the same call shape works on both TFMs without any per-TFM cost.
    //   - StreamReader.ReadToEndAsync() with a boundary cancel check matches
    //     net10's ReadToEndAsync(CT) for our use case (small agent files);
    //     mid-read cancellation isn't observable for sub-second reads.
    public static async Task WriteAsync(this IFileAccessor writer, AgentFilePath path, string contents, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();

        using var stream = writer.OpenWrite(path);
        using TextWriter sw = new StreamWriter(stream, Encoding.UTF8);
        await sw.WriteAsync(contents).ConfigureAwait(false);
    }

    public static async Task WriteAsync(this IFileAccessor writer, AgentFilePath path, byte[] bytes, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();

        using var stream = writer.OpenWrite(path);

        await stream.WriteAsync(bytes, 0, bytes.Length, cancel).ConfigureAwait(false);
    }

    public static async Task<string> ReadStringAsync(this IFileAccessor writer, AgentFilePath path, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();

        using var stream = writer.OpenRead(path);
        using var sr = new StreamReader(stream);

        return await sr.ReadToEndAsync().ConfigureAwait(false);
    }

    public static async Task<byte[]> ReadBytesAsync(this IFileAccessor reader, AgentFilePath path, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();

        using var stream = reader.OpenRead(path);
        using var buffer = new MemoryStream();

        await stream.CopyToAsync(buffer, 81920, cancel).ConfigureAwait(false);

        return buffer.ToArray();
    }

    public static void Write(this IFileAccessor writer, AgentFilePath path, ReadOnlyMemory<byte> content)
    {
        using var stream = writer.OpenWrite(path);

#if NETSTANDARD2_0
        if (System.Runtime.InteropServices.MemoryMarshal.TryGetArray(content, out var segment) && segment.Array is { } array)
        {
            stream.Write(array, segment.Offset, segment.Count);

            return;
        }

        var copy = content.ToArray();
        stream.Write(copy, 0, copy.Length);
#else
        stream.Write(content.Span);
#endif
    }

    public static ReadOnlyMemory<byte> ReadBytes(this IFileAccessor reader, AgentFilePath path)
    {
        using var stream = reader.OpenRead(path);
        using var content = new MemoryStream(stream.CanSeek ? (int)stream.Length : 0);
        stream.CopyTo(content);

        return new ReadOnlyMemory<byte>(content.GetBuffer(), 0, (int)content.Length);
    }

    public static byte[]? EditText(ReadOnlyMemory<byte> content, Func<string, string> edit)
    {
        var bytes = content.ToArray();
        var marked = bytes.Take(ByteOrderMark.Length).SequenceEqual(ByteOrderMark);
        var text = Encoding.UTF8.GetString(bytes, marked ? ByteOrderMark.Length : 0, bytes.Length - (marked ? ByteOrderMark.Length : 0));
        var edited = edit(text);

        return string.Equals(edited, text, StringComparison.Ordinal) ? null : (marked ? ByteOrderMark : Array.Empty<byte>()).Concat(Encoding.UTF8.GetBytes(edited)).ToArray();
    }
}
