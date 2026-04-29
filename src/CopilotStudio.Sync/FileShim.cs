// Copyright (C) Microsoft Corporation. All rights reserved.

using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.CopilotStudio.Sync;

internal static class FileShim
{
    /// <summary>
    /// Reads a file's text contents asynchronously, with the best async I/O
    /// available on each target framework.
    /// </summary>
    /// <remarks>
    /// On net10, dispatches to File.ReadAllTextAsync which opens the underlying
    /// FileStream with useAsync:true and performs real async I/O. On
    /// netstandard2.0, falls back to a StreamReader-based read because
    /// File.ReadAllTextAsync is unavailable; cancellation is checked once at
    /// boundary rather than mid-read, which is acceptable for the small config
    /// files this shim's call sites read (workflow JSON/YAML).
    /// </remarks>
    public static async Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken)
    {
#if NETSTANDARD2_0
        cancellationToken.ThrowIfCancellationRequested();
        using var sr = new StreamReader(path);
        return await sr.ReadToEndAsync().ConfigureAwait(false);
#else
        return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
#endif
    }

    public static async Task<string> ReadAllTextAsync(string path, Encoding encoding, CancellationToken cancellationToken)
    {
#if NETSTANDARD2_0
        cancellationToken.ThrowIfCancellationRequested();
        using var sr = new StreamReader(path, encoding);
        return await sr.ReadToEndAsync().ConfigureAwait(false);
#else
        return await File.ReadAllTextAsync(path, encoding, cancellationToken).ConfigureAwait(false);
#endif
    }
}
