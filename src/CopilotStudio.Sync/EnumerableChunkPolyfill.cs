// Copyright (C) Microsoft Corporation. All rights reserved.

#if NETSTANDARD2_0
using System.Collections.Generic;

namespace System.Linq;

internal static class EnumerableChunkPolyfill
{
    public static IEnumerable<TSource[]> Chunk<TSource>(this IEnumerable<TSource> source, int size)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        if (size < 1) throw new ArgumentOutOfRangeException(nameof(size));

        TSource[]? buffer = null;
        var count = 0;
        foreach (var item in source)
        {
            buffer ??= new TSource[size];
            buffer[count++] = item;
            if (count == size)
            {
                yield return buffer;
                buffer = null;
                count = 0;
            }
        }

        if (count > 0)
        {
            Array.Resize(ref buffer, count);
            yield return buffer!;
        }
    }
}
#endif
