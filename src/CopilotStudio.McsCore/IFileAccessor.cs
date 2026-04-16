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
    public static async Task WriteAsync(this IFileAccessor writer, AgentFilePath path, string contents, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();

        using var stream = writer.OpenWrite(path);
        using TextWriter sw = new StreamWriter(stream, Encoding.UTF8);
        await sw.WriteAsync(contents.AsMemory(), cancel).ConfigureAwait(false);
    }

    public static async Task WriteAsync(this IFileAccessor writer, AgentFilePath path, byte[] bytes, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();

        using var stream = writer.OpenWrite(path);

        await stream.WriteAsync(bytes, cancel).ConfigureAwait(false);
    }

    public static async Task<string> ReadStringAsync(this IFileAccessor writer, AgentFilePath path, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();

        using var stream = writer.OpenRead(path);
        using var sr = new StreamReader(stream);

        var str = await sr.ReadToEndAsync(cancel).ConfigureAwait(false);
        return str;
    }
}
