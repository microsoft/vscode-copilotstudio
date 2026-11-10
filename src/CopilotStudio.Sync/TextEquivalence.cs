// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Text;

namespace Microsoft.CopilotStudio.Sync;

internal sealed class TextEquivalence
{
    internal static readonly TextEquivalence Checkout = new(skipsByteOrderMark: true, loneCarriageReturnBreaksLine: false, finalLineBreakOptional: false);

    internal static readonly TextEquivalence Merge = new(skipsByteOrderMark: false, loneCarriageReturnBreaksLine: true, finalLineBreakOptional: true);

    private readonly bool _loneCarriageReturnBreaksLine;

    private readonly bool _finalLineBreakOptional;

    private TextEquivalence(bool skipsByteOrderMark, bool loneCarriageReturnBreaksLine, bool finalLineBreakOptional)
    {
        Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: skipsByteOrderMark, throwOnInvalidBytes: true);
        _loneCarriageReturnBreaksLine = loneCarriageReturnBreaksLine;
        _finalLineBreakOptional = finalLineBreakOptional;
    }

    internal Encoding Encoding { get; }

    internal TextReader Open(Stream content) => new StreamReader(content, Encoding, detectEncodingFromByteOrderMarks: false);

    internal bool Same(TextReader file, TextReader candidate)
    {
        var fileText = new LineBreaks(this, file);
        var candidateText = new LineBreaks(this, candidate);
        int next;

        do
        {
            next = fileText.Read();
            if (next == '\0' || next != candidateText.Read())
            {
                return false;
            }
        }
        while (next >= 0);

        return true;
    }

    internal string? Decode(ReadOnlyMemory<byte> content)
    {
        try
        {
            using var reader = Open(new MemoryStream(content.ToArray(), writable: false));
            var text = reader.ReadToEnd();

            return text.IndexOf('\0') < 0 ? text : null;
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    internal string Normalize(string text)
    {
        var lines = new LineBreaks(this, new StringReader(text));
        var normalized = new StringBuilder(text.Length + 1);

        for (var next = lines.Read(); next >= 0; next = lines.Read())
        {
            normalized.Append((char)next);
        }

        return normalized.ToString();
    }

    internal static string InLineEndingsOf(string text, string? sample) => sample != null && sample.Contains("\r\n") ? text.Replace("\n", "\r\n") : text;

    private sealed class LineBreaks
    {
        private const int Nothing = -2;

        private readonly TextEquivalence _rules;

        private readonly TextReader _reader;

        private int _pending = Nothing;

        private int _last = -1;

        internal LineBreaks(TextEquivalence rules, TextReader reader)
        {
            _rules = rules;
            _reader = reader;
        }

        internal int Read()
        {
            var next = _pending == Nothing ? _reader.Read() : _pending;
            _pending = next == '\r' ? _reader.Read() : Nothing;

            if (_pending == '\n')
            {
                _pending = Nothing;
                next = '\n';
            }
            else if (next == '\r' && _rules._loneCarriageReturnBreaksLine)
            {
                next = '\n';
            }
            else if (next < 0 && _rules._finalLineBreakOptional && _last >= 0 && _last != '\n')
            {
                next = '\n';
            }

            _last = next;

            return next;
        }
    }
}
