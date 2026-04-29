// Copyright (C) Microsoft Corporation. All rights reserved.

using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.CopilotStudio.Sync;

internal static class FileShim
{
    // No #if needed: net10's File.ReadAllTextAsync is itself implemented as a
    // StreamReader+ReadToEndAsync internally, so this LCD form is functionally
    // identical. Cancellation is checked at boundary rather than mid-read; the
    // call sites read small config files (workflow JSON/YAML), where mid-read
    // cancellation has no practical value.
    public static async Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var sr = new StreamReader(path);
        return await sr.ReadToEndAsync().ConfigureAwait(false);
    }

    public static async Task<string> ReadAllTextAsync(string path, Encoding encoding, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var sr = new StreamReader(path, encoding);
        return await sr.ReadToEndAsync().ConfigureAwait(false);
    }
}
