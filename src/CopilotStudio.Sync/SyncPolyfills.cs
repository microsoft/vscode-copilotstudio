// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Sync-local polyfills for APIs added after netstandard2.0. McsCore polyfills
// for string extensions and ToHashSet are also visible here via InternalsVisibleTo.

using System.Net.Http;

namespace Microsoft.CopilotStudio.Sync
{
    internal static class HttpMethodHelper
    {
        // No #if needed: `new HttpMethod("PATCH")` produces an HttpMethod
        // semantically identical to the net10-only HttpMethod.Patch static.
        // Identity is by Method string, so a single instance suffices on both TFMs.
        public static readonly HttpMethod Patch = new HttpMethod("PATCH");
    }
}

#if NETSTANDARD2_0
namespace System.Net.Http
{
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;

    internal static class HttpContentPolyfills
    {
        public static async Task<string> ReadAsStringAsync(this HttpContent content, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await content.ReadAsStringAsync().ConfigureAwait(false);
        }

        public static async Task<Stream> ReadAsStreamAsync(this HttpContent content, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await content.ReadAsStreamAsync().ConfigureAwait(false);
        }
    }
}

namespace System.Security.Cryptography
{
    internal static class RandomNumberGeneratorPolyfill
    {
        public static int GetInt32(int toExclusive)
        {
            if (toExclusive <= 0) throw new ArgumentOutOfRangeException(nameof(toExclusive));

            using var rng = RandomNumberGenerator.Create();
            var bytes = new byte[4];
            var range = (uint)toExclusive;
            var mask = uint.MaxValue - (uint.MaxValue % range);
            uint result;
            do
            {
                rng.GetBytes(bytes);
                result = BitConverter.ToUInt32(bytes, 0);
            } while (result >= mask);
            return (int)(result % range);
        }
    }
}
#endif
