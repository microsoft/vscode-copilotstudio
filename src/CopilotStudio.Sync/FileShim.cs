// Copyright (C) Microsoft Corporation. All rights reserved.

using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.CopilotStudio.Sync;

internal static class FileShim
{
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
