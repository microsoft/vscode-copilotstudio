// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Text;
using Microsoft.CopilotStudio.McsCore;

namespace Microsoft.CopilotStudio.Sync;

internal static class CheckoutContent
{
    private const int ChunkSize = 81920;

    internal static bool HoldsText(IFileAccessor fileAccessor, AgentFilePath path, string text) =>
        fileAccessor.Exists(path) && Compare(() => SameText(fileAccessor, path, new StringReader(text)));

    internal static bool HoldsContentOf(IFileAccessor fileAccessor, AgentFilePath path, AgentFilePath candidatePath) =>
        fileAccessor.Exists(path) && Compare(() => SameBytes(fileAccessor, path, candidatePath) || SameText(fileAccessor, path, TextEquivalence.Checkout.Open(fileAccessor.OpenRead(candidatePath))));

    private static bool Compare(Func<bool> comparison)
    {
        try
        {
            return comparison();
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool SameText(IFileAccessor fileAccessor, AgentFilePath path, TextReader candidate)
    {
        using (candidate)
        using (var file = TextEquivalence.Checkout.Open(fileAccessor.OpenRead(path)))
        {
            return TextEquivalence.Checkout.Same(file, candidate);
        }
    }

    private static bool SameBytes(IFileAccessor fileAccessor, AgentFilePath path, AgentFilePath candidatePath)
    {
        using var file = fileAccessor.OpenRead(path);
        using var candidate = fileAccessor.OpenRead(candidatePath);
        var fileChunk = new byte[ChunkSize];
        var candidateChunk = new byte[ChunkSize];
        int count;

        do
        {
            count = Fill(file, fileChunk);
            if (count != Fill(candidate, candidateChunk) || !fileChunk.AsSpan(0, count).SequenceEqual(candidateChunk.AsSpan(0, count)))
            {
                return false;
            }
        }
        while (count > 0);

        return true;
    }

    private static int Fill(Stream stream, byte[] chunk)
    {
        var filled = 0;
        int read;

        while (filled < chunk.Length && (read = stream.Read(chunk, filled, chunk.Length - filled)) > 0)
        {
            filled += read;
        }

        return filled;
    }
}
