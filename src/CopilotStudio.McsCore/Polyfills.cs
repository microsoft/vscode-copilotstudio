// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Polyfills for string APIs added after netstandard2.0. Same-namespace
// internal extension methods take precedence over the BCL's internal
// equivalents (which are invisible across assembly boundaries).

#if NETSTANDARD2_0
using System.Collections.Generic;

namespace System;

internal static class StringPolyfills
{
    public static bool Contains(this string source, string value, StringComparison comparisonType)
        => source.IndexOf(value, comparisonType) >= 0;

    public static bool StartsWith(this string source, char value)
        => source.Length > 0 && source[0] == value;

    public static bool EndsWith(this string source, char value)
        => source.Length > 0 && source[source.Length - 1] == value;
}

internal static class EnumerableToHashSetPolyfill
{
    public static HashSet<TSource> ToHashSet<TSource>(this IEnumerable<TSource> source)
        => new HashSet<TSource>(source);

    public static HashSet<TSource> ToHashSet<TSource>(this IEnumerable<TSource> source, IEqualityComparer<TSource>? comparer)
        => new HashSet<TSource>(source, comparer);
}
#endif
