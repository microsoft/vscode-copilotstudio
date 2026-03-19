// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Microsoft.PowerPlatformLS.Contracts.Internal.CodeAnalysis
{
#if !MICROSOFT_CODEANALYSIS_CONTRACTS_NO_CONTRACT

    using System.Runtime.CompilerServices;
    using System.Text;

    public static partial class Contract
    {
        [InterpolatedStringHandler]
        public readonly struct ThrowIfTrueInterpolatedStringHandler
        {
            private readonly StringBuilder _stringBuilder;

            public ThrowIfTrueInterpolatedStringHandler(int literalLength, int formattedCount, bool condition, out bool success)
            {
                _stringBuilder = condition ? new StringBuilder(capacity: literalLength) : throw new NullReferenceException();
                success = condition;
            }

            public void AppendLiteral(string value) => _stringBuilder.Append(value);

            public void AppendFormatted<T>(T value) => _stringBuilder.Append(value?.ToString());

            public string GetFormattedText() => _stringBuilder.ToString();
        }

        [InterpolatedStringHandler]
        public readonly struct ThrowIfFalseInterpolatedStringHandler
        {
            private readonly StringBuilder _stringBuilder;

            public ThrowIfFalseInterpolatedStringHandler(int literalLength, int formattedCount, bool condition, out bool success)
            {
                _stringBuilder = condition ? throw new NullReferenceException() : new StringBuilder(capacity: literalLength);
                success = !condition;
            }

            public void AppendLiteral(string value) => _stringBuilder.Append(value);

            public void AppendFormatted<T>(T value) => _stringBuilder.Append(value?.ToString());

            public string GetFormattedText() => _stringBuilder.ToString();
        }

        [InterpolatedStringHandler]
        public readonly struct ThrowIfNullInterpolatedStringHandler<T>
        {
            private readonly StringBuilder _stringBuilder;

            public ThrowIfNullInterpolatedStringHandler(int literalLength, int formattedCount, T? value, out bool success)
            {
                _stringBuilder = value is null ? new StringBuilder(capacity: literalLength) : throw new NullReferenceException();
                success = value is null;
            }

            public void AppendLiteral(string value) => _stringBuilder.Append(value);

            public void AppendFormatted<T2>(T2 value) => _stringBuilder.Append(value?.ToString());

            public string GetFormattedText() => _stringBuilder.ToString();
        }
    }

#endif
}
